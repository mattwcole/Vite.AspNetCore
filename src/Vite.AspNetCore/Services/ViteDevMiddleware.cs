﻿// Copyright (c) 2023 Quetzal Rivera.
// Licensed under the MIT License, See LICENCE in the project root for license information.

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Text.RegularExpressions;
using Vite.AspNetCore.Utilities;

namespace Vite.AspNetCore.Services;

/// <summary>
/// Represents a middleware that proxies requests to the Vite Dev Server.
/// </summary>
public class ViteDevMiddleware : IMiddleware, IDisposable
{
	private readonly ILogger<ViteDevMiddleware> _logger;
    private readonly IHttpClientFactory _clientFactory;
	private readonly string _viteServerBaseUrl;
	private NodeScriptRunner? _scriptRunner;
	private readonly ViteOptions _viteOptions;

	private static readonly Regex ViteReadyRegex =
		new(@"^\s*VITE\sv\d+\.\d+\.\d+\s+ready\sin\s\d+(?:\.\d+)?\s\w+$", RegexOptions.Compiled);

	private static readonly Regex VitePortInUse =
		new(@"Error: Port (?<port>\d+) is already in use", RegexOptions.Compiled);

	/// <summary>
	/// Optional. This flag is used to know if the middleware is waiting for the Vite development server to start.
	/// </summary>
	private bool _waitForDevServer;

	/// <summary>
	/// Initializes a new instance of the <see cref="ViteDevMiddleware"/> class.
	/// </summary>
	/// <param name="logger">The <see cref="ILogger{ViteDevMiddleware}"/> instance.</param>
	/// <param name="configuration">The <see cref="IConfiguration"/> instance.</param>
	/// <param name="environment">The <see cref="IWebHostEnvironment"/> instance.</param>
	/// <param name="lifetime">The <see cref="IHostApplicationLifetime"/> instance.</param>
	public ViteDevMiddleware(ILogger<ViteDevMiddleware> logger, IConfiguration configuration,
		IWebHostEnvironment environment, IHostApplicationLifetime lifetime, IHttpClientFactory clientFactory)
	{
		ViteStatusService.IsDevServerRunning = true;
		// Set the logger.
		this._logger = logger;
		// Set the http client factory
        this._clientFactory = clientFactory;

		// Read the Vite options from the configuration.
		this._viteOptions = ViteStatusService.Options ?? configuration
			.GetSection(ViteOptions.Vite)
			.Get<ViteOptions>() ?? new ViteOptions();
		// Get the port and host from the configuration.
		var port = this._viteOptions.Server.Port;
		var host = this._viteOptions.Server.Host;
		// Check if https is enabled.
		var https = this._viteOptions.Server.Https;
		// Build the base url.
		this._viteServerBaseUrl = $"{(https ? "https" : "http")}://{host}:{port}";

		// Prepare and run the Vite Dev Server if AutoRun is true and the middleware is enabled.
		if (this._viteOptions.Server.AutoRun && ViteStatusService.IsMiddlewareRegistered)
		{
			// Set the waitForDevServer flag to true.
			this._waitForDevServer = true;
			// Gets the package manager command.
			var pkgManagerCommand = this._viteOptions.PackageManager;
			// Gets the working directory.
			var workingDirectory = this._viteOptions.PackageDirectory ?? environment.ContentRootPath;
			// Gets the script name.= to run the Vite Dev Server.
			var scriptName = this._viteOptions.Server.ScriptName;

			if (this._viteOptions.Server.KillPort)
			{
				// pre-emptively try to clear the port
				PidUtils.KillPort(port);
			}

			// Create a new instance of the NodeScriptRunner class.
			this._scriptRunner = new NodeScriptRunner(logger, pkgManagerCommand, scriptName, workingDirectory,
				lifetime.ApplicationStopping);

			// Wait for the processes standard output
			void OnInfo(string line)
			{
				// Check if the line contains the base url.
				if (!ViteReadyRegex.IsMatch(line))
				{
					return;
				}

				logger.LogInformation(
					"The vite development server was started. Starting to proxy requests to '{ViteServerBaseUrl}'",
					this._viteServerBaseUrl);

				// start logging errors now
				this._scriptRunner.StdErrorReader.IsLoggingEnabled = true;
				// Set the waitForDevServer flag to false
				this._waitForDevServer = false;
			}

			// Wait for any error messages
			void OnError(string line)
			{
				// If the port is used by another process, kill it and start a new _scriptRunner.
				if (this._viteOptions.Server.KillPort)
				{
					var portInUse = VitePortInUse.Match(line);
					if (!portInUse.Success)
					{
						return;
					}

					var portNumber = ushort.Parse(portInUse.Groups["port"].Value);
					logger.LogInformation("The vite development server is already running, attempting to kill previous process at {Port}", portNumber);
					PidUtils.KillPort(portNumber);

					// attempt to start a new _scriptRunner
					var nodeScriptRunner = this._scriptRunner;
					if (nodeScriptRunner != null)
					{
						nodeScriptRunner.StdErrorReader.IsLoggingEnabled = false;
						nodeScriptRunner.StdErrorReader.OnReceivedLine -= OnError;
						nodeScriptRunner.StdOutReader.OnReceivedLine -= OnInfo;
						((IDisposable)nodeScriptRunner).Dispose();
					}

					this._scriptRunner = new NodeScriptRunner(logger, pkgManagerCommand, scriptName, workingDirectory,
						lifetime.ApplicationStopping);
					this._scriptRunner.StdOutReader.OnReceivedLine += OnInfo;
					this._scriptRunner.StdErrorReader.OnReceivedLine += OnError;
				}
			}

			this._scriptRunner.StdOutReader.OnReceivedLine += OnInfo;
			this._scriptRunner.StdErrorReader.OnReceivedLine += OnError;

			logger.LogInformation(
				"The middleware has called the dev script. It may take a few seconds before the Vite Dev Server becomes available");
		}
	}

	/// <inheritdoc />
	public async Task InvokeAsync(HttpContext context, RequestDelegate next)
	{
		// If the middleware is not registered, call the next middleware.
		if (!ViteStatusService.IsMiddlewareRegistered)
		{
			this._logger.LogWarning(
				"Oops, did you forgot to register the middleware? Use app.UseViteDevMiddleware() in your Startup.cs or Program.cs file");
			await next(context);
			return;
		}

		// If the request doesn't have an endpoint, the request path is not null and the request method is GET, proxy the request to the Vite Development Server.
		if (context.GetEndpoint() == null && context.Request.Path.HasValue &&
			context.Request.Method == HttpMethod.Get.Method)
		{
			// Get the request path
			var path = context.Request.Path.Value;
			// Proxy the request to the Vite Dev Server.
			await this.ProxyAsync(context, next, path);
		}
		// If the request path is null, call the next middleware.
		else
		{
			await next(context);
		}
	}

	/// <summary>
	/// Proxies the request to the Vite Dev Server.
	/// </summary>
	/// <param name="context">The <see cref="HttpContext"/> instance.</param>
	/// <param name="next">The next handler in the request pipeline</param>
	/// <param name="path">The request path.</param>
	/// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
	private async Task ProxyAsync(HttpContext context, RequestDelegate next, string path)
	{
		// Initialize a "new" instance of the HttpClient class via the HttpClientFactory.
		var client = _clientFactory.CreateClient();
		client.BaseAddress = new Uri(this._viteServerBaseUrl);

		// Pass "Accept" header from the original request.
		if (context.Request.Headers.ContainsKey("Accept"))
		{
			client.DefaultRequestHeaders.Add("Accept", context.Request.Headers.Accept.ToList());
		}

		// If the waitForDevServer flag is true, wait for the Vite development server to start.
		if (this._waitForDevServer)
		{
			// Wait for the server to start until the timeout is reached or the server is found.
			var timeout = TimeSpan.FromSeconds(this._viteOptions.Server.TimeOut);
			// Smaller increments mean faster discover but potentially more loops
			var increment = TimeSpan.FromMilliseconds(200);
			var waiting = new TimeSpan(0);

			// Wait for the server to start until the timeout is reached or the server is found.
			while (this._waitForDevServer && waiting < timeout)
			{
				waiting = waiting.Add(increment);
				await Task.Delay(increment);
			}

			// If the waitForDevServer flag is true, log the timeout.
			if (this._waitForDevServer)
			{
				this._logger.LogWarning("The Vite development server did not start within {TotalSeconds} seconds",
					timeout.TotalSeconds);
				// Set the waitForDevServer flag to false.
				this._waitForDevServer = false;
			}
		}

		try
		{
			// Get the requested path from the Vite Dev Server.
			var response = await client.GetAsync(path);
			// If the response is successful, process.
			if (response.IsSuccessStatusCode)
			{
				// Get the response content.
				var content = await response.Content.ReadAsByteArrayAsync();
				// Get the response content type.
				var contentType = response.Content.Headers.ContentType?.MediaType;
				// Set the response content type.
				context.Response.ContentType = contentType ?? "application/octet-stream";
				// Set the response content length.
				context.Response.ContentLength = content.Length;
				// Write the response content.
				await context.Response.Body.WriteAsync(content);
			}
			// Otherwise, call the next middleware.
			else
			{
				await next(context);
			}
		}
		catch (HttpRequestException exp)
		{
			// Log the exception.
			this._logger.LogWarning("{Message}. Make sure the Vite development server is running", exp.Message);
			await next(context);
		}
	}

	void IDisposable.Dispose()
	{
		// If the script runner was defined, dispose it.
		if (this._scriptRunner != null)
		{
			((IDisposable)this._scriptRunner).Dispose();
		}
	}
}