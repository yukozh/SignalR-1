﻿// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Channels;
using Microsoft.AspNetCore.Sockets.Internal;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Microsoft.AspNetCore.Sockets.Client
{
    public class LongPollingTransport : ITransport
    {
        private readonly HttpClient _httpClient;
        private readonly ILogger _logger;
        private Channel<byte[], SendMessage> _application;
        private Task _sender;
        private Task _poller;

        private readonly CancellationTokenSource _transportCts = new CancellationTokenSource();

        public Task Running { get; private set; } = Task.CompletedTask;

        public LongPollingTransport(HttpClient httpClient)
            : this(httpClient, null)
        { }

        public LongPollingTransport(HttpClient httpClient, ILoggerFactory loggerFactory)
        {
            _httpClient = httpClient;
            _logger = (loggerFactory ?? NullLoggerFactory.Instance).CreateLogger<LongPollingTransport>();
        }

        public Task StartAsync(Uri url, Channel<byte[], SendMessage> application)
        {
            _logger.StartTransport();

            _application = application;

            // Start sending and polling (ask for binary if the server supports it)
            _poller = Poll(url, _transportCts.Token);
            _sender = SendUtils.SendMessages(url, _application, _httpClient, _transportCts, _logger);

            Running = Task.WhenAll(_sender, _poller).ContinueWith(t =>
            {
                _logger.TransportStopped(t.Exception?.InnerException);
                _application.Out.TryComplete(t.IsFaulted ? t.Exception.InnerException : null);
                return t;
            }).Unwrap();

            return Task.CompletedTask;
        }

        public async Task StopAsync()
        {
            _logger.TransportStopping();

            _transportCts.Cancel();

            try
            {
                await Running;
            }
            catch
            {
                // exceptions have been handled in the Running task continuation by closing the channel with the exception
            }
        }

        private async Task Poll(Uri pollUrl, CancellationToken cancellationToken)
        {
            _logger.StartReceive();
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    var request = new HttpRequestMessage(HttpMethod.Get, pollUrl);
                    request.Headers.UserAgent.Add(SendUtils.DefaultUserAgentHeader);

                    var response = await _httpClient.SendAsync(request, cancellationToken);
                    response.EnsureSuccessStatusCode();

                    if (response.StatusCode == HttpStatusCode.NoContent || cancellationToken.IsCancellationRequested)
                    {
                        _logger.ClosingConnection();

                        // Transport closed or polling stopped, we're done
                        break;
                    }
                    else
                    {
                        _logger.ReceivedMessages();

                        // Until Pipeline starts natively supporting BytesReader, this is the easiest way to do this.
                        var payload = await response.Content.ReadAsByteArrayAsync();
                        if (payload.Length > 0)
                        {
                            while (!_application.Out.TryWrite(payload))
                            {
                                if (cancellationToken.IsCancellationRequested || !await _application.Out.WaitToWriteAsync(cancellationToken))
                                {
                                    return;
                                }
                            }
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // transport is being closed
                _logger.ReceiveCanceled();
            }
            catch (Exception ex)
            {
                _logger.ErrorPolling(pollUrl, ex);
                throw;
            }
            finally
            {
                // Make sure the send loop is terminated
                _transportCts.Cancel();
                _logger.ReceiveStopped();
            }
        }
    }
}
