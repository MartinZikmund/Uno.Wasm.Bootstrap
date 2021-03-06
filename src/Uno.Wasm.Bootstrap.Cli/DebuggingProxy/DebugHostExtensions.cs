// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

//
// Imported from https://github.com/aspnet/Blazor/tree/release/0.7.0
//

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

#if NETCOREAPP3_1
using WebAssembly.Net.Debugging;
#elif NET5_0
using Microsoft.WebAssembly.Diagnostics;
#endif

namespace Uno.Wasm.Bootstrap.Cli.DebuggingProxy
{
	internal static class DebuggerHostExtensions
	{
		public static void UseDebugHost(this IApplicationBuilder app)
		{
			app.UseWebSockets();

			app.Use((context, next) =>
			{
				var requestPath = context.Request.Path;
				if (!requestPath.StartsWithSegments("/_framework/debug"))
				{
					return next();
				}

				if (requestPath.Equals("/_framework/debug/ws-proxy", StringComparison.OrdinalIgnoreCase))
				{
					return DebugWebSocketProxyRequest(app.ApplicationServices.GetRequiredService<ILoggerFactory>(), context);
				}

				if (requestPath.Equals("/_framework/debug", StringComparison.OrdinalIgnoreCase))
				{
					return DebugHome(context);
				}

				context.Response.StatusCode = (int)HttpStatusCode.NotFound;
				return Task.CompletedTask;
			});
		}

		private static async Task DebugWebSocketProxyRequest(ILoggerFactory loggerFactory, HttpContext context)
		{
			if (!context.WebSockets.IsWebSocketRequest)
			{
				context.Response.StatusCode = 400;
				return;
			}

			var browserUri = new Uri(context.Request.Query["browser"]);
			var ideSocket = await context.WebSockets.AcceptWebSocketAsync();
			await GetProxy(loggerFactory).Run(browserUri, ideSocket);
		}

		private static DebuggerProxy GetProxy(ILoggerFactory loggerFactory) =>
#if NETCOREAPP3_1
			new DebuggerProxy(loggerFactory);
#elif NET5_0
			new DebuggerProxy(loggerFactory, new List<string>());
#endif

		private static async Task DebugHome(HttpContext context)
		{
			context.Response.ContentType = "text/html";

			var request = context.Request;
			var appRootUrl = $"{request.Scheme}://{request.Host}{request.PathBase}/";
			var targetTabUrl = request.Query["url"];
			if (string.IsNullOrEmpty(targetTabUrl))
			{
				context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
				await context.Response.WriteAsync("No value specified for 'url'");
				return;
			}

			// TODO: Allow overriding port (but not hostname, as we're connecting to the
			// local browser, not to the webserver serving the app)
			var debuggerHost = "http://localhost:9222";
			var debuggerTabsListUrl = $"{debuggerHost}/json";
			IEnumerable<BrowserTab> availableTabs;

			try
			{
				availableTabs = await GetOpenedBrowserTabs(debuggerHost);
			}
			catch (Exception ex)
			{

				await context.Response.WriteAsync($@"
    <h1>Unable to find debuggable browser tab</h1>
    <p>
        Could not get a list of browser tabs from <code>{debuggerTabsListUrl}</code>.
        Ensure Chrome is running with debugging enabled.
    </p>
    <h2>Resolution</h2>
    {GetLaunchChromeInstructions(appRootUrl)}
    <p>... then use that new tab for debugging.</p>
    <h2>Underlying exception:</h2>
    <pre>{ex}</pre>
");
				return;
			}

			var matchingTabs = availableTabs
				.Where(t => t.Url.Equals(targetTabUrl, StringComparison.Ordinal))
				.ToList();
			if (matchingTabs.Count == 0)
			{
				await context.Response.WriteAsync($@"
                    <h1>Unable to find debuggable browser tab</h1>
                    <p>
                        The response from <code>{debuggerTabsListUrl}</code> does not include
                        any entry for <code>{targetTabUrl}</code>.
                    </p>");
				return;
			}
			else if (matchingTabs.Count > 1)
			{
				// TODO: Automatically disambiguate by adding a GUID to the page title
				// when you press the debugger hotkey, include it in the querystring passed
				// here, then remove it once the debugger connects.
				await context.Response.WriteAsync($@"
                    <h1>Multiple matching tabs are open</h1>
                    <p>
                        There is more than one browser tab at <code>{targetTabUrl}</code>.
                        Close the ones you do not wish to debug, then refresh this page.
                    </p>");
				return;
			}

			// Now we know uniquely which tab to debug, construct the URL to the debug
			// page and redirect there
			var tabToDebug = matchingTabs.Single();
			var underlyingV8Endpoint = tabToDebug.WebSocketDebuggerUrl;
			var proxyEndpoint = $"{request.Host}{request.PathBase}/_framework/debug/ws-proxy?browser={WebUtility.UrlEncode(underlyingV8Endpoint)}";
			var devToolsUrlAbsolute = new Uri(debuggerHost + tabToDebug.DevtoolsFrontendUrl);
			var devToolsUrlWithProxy = $"{devToolsUrlAbsolute.Scheme}://{devToolsUrlAbsolute.Authority}{devToolsUrlAbsolute.AbsolutePath}?ws={proxyEndpoint}";
			context.Response.Redirect(devToolsUrlWithProxy);
		}

		private static string GetLaunchChromeInstructions(string appRootUrl)
		{
			if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
			{
				return $@"<p>Close all browser instances, then restart it with the debug server enabled. Press Win+R and enter the following:</p>
							<h4>Google Chrome</h4>
							<p><strong><code>taskkill /IM chrome* /F /T<br />""%programfiles(x86)%\Google\Chrome\Application\chrome.exe"" --remote-debugging-port=9222 {appRootUrl}</code></strong></p>
							<p>Or with an isolated profile: <strong><code>""%programfiles(x86)%\Google\Chrome\Application\chrome.exe"" --remote-debugging-port=9222 --user-data-dir=""%TEMP%\ChromeDebug"" {appRootUrl}</code></strong></p>
							<h4>Microsoft Edge (Chromium)</h4>
							<p><strong><code>taskkill /IM msedge* /F /T<br />""%programfiles(x86)%\Microsoft\Edge Dev\Application\msedge.exe"" --remote-debugging-port=9222 {appRootUrl}</code></strong></p>
							<p>Or with an isolated profile: <strong><code>""%programfiles(x86)%\Microsoft\Edge Dev\Application\msedge.exe"" --remote-debugging-port=9222 --user-data-dir=""%TEMP%\EdgeDebug"" {appRootUrl}</code></strong></p>";
			}
			else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
			{
				return $@"<p>Close all Chrome instances, then in a terminal window execute the following:</p>
                          <p><strong><code>google-chrome --remote-debugging-port=9222 {appRootUrl}</code></strong></p>";
			}
			else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
			{
				return $@"<p>Close all Chrome instances, then in a terminal window execute the following:</p>
                          <p><strong><code>open /Applications/Google\ Chrome.app --args --remote-debugging-port=9222 {appRootUrl}</code></strong></p>";
			}
			else
			{
				throw new InvalidOperationException("Unknown OS platform");
			}
		}

		private static async Task<IEnumerable<BrowserTab>> GetOpenedBrowserTabs(string debuggerHost)
		{
			using (var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(5) })
			{
				var jsonResponse = await httpClient.GetStringAsync($"{debuggerHost}/json");
				return JsonConvert.DeserializeObject<BrowserTab[]>(jsonResponse);
			}
		}

		class BrowserTab
		{
			public string Type { get; set; }
			public string Url { get; set; }
			public string Title { get; set; }
			public string DevtoolsFrontendUrl { get; set; }
			public string WebSocketDebuggerUrl { get; set; }
		}
	}

}
