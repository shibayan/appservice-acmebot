using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.Tracing;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace AzureAppService.LetsEncrypt.Internal
{
    internal class DurableTaskEventListener : EventListener
    {
        public DurableTaskEventListener()
        {
            _outputTask = Task.Run(ProcessMessageQueue);
        }

        protected override void OnEventWritten(EventWrittenEventArgs eventData)
        {
            // 完了と失敗イベント以外は無視する
            if (eventData.EventName != "FunctionCompleted" && eventData.EventName != "FunctionFailed")
            {
                return;
            }

            var payload = new Dictionary<string, object>(eventData.Payload.Count);

            for (int i = 0; i < eventData.Payload.Count; i++)
            {
                payload[eventData.PayloadNames[i]] = eventData.Payload[i];
            }

            // オーケストレーター以外のイベントは無視する
            if ((string)payload["FunctionType"] != "Orchestrator")
            {
                return;
            }

            _messageQueue.Add($"{eventData.EventName} : {payload["FunctionName"]}");
        }

        private readonly TimeSpan _interval = TimeSpan.FromSeconds(5);
        private bool _disposed;

        private readonly Task _outputTask;
        private readonly BlockingCollection<string> _messageQueue = new BlockingCollection<string>(new ConcurrentQueue<string>());
        private readonly CancellationTokenSource _cancellationTokenSource = new CancellationTokenSource();

        private async Task ProcessMessageQueue()
        {
            while (!_cancellationTokenSource.IsCancellationRequested)
            {
                if (_messageQueue.TryTake(out var message))
                {
                    try
                    {
                        await _httpClient.PostAsync(Settings.Default.Webhook, new StringContent(message));
                    }
                    catch
                    {
                        // ignored
                    }
                }
                else
                {
                    await Task.Delay(_interval, _cancellationTokenSource.Token);
                }
            }
        }

        public override void Dispose()
        {
            base.Dispose();

            if (!_disposed)
            {
                _cancellationTokenSource.Cancel();
                _messageQueue.CompleteAdding();

                try
                {
                    _outputTask.Wait(_interval);
                }
                catch (TaskCanceledException)
                {
                    // ignored
                }
                catch (AggregateException ex) when (ex.InnerExceptions.Count == 0 && ex.InnerExceptions[0] is TaskCanceledException)
                {
                    // ignored
                }

                _disposed = true;
            }
        }

        private static readonly HttpClient _httpClient = new HttpClient();

        private static bool _startWasCalled;

        public static void Start()
        {
            if (_startWasCalled)
            {
                return;
            }

            if (string.IsNullOrEmpty(Settings.Default.Webhook))
            {
                _startWasCalled = true;

                return;
            }

            var eventSource = EventSource.GetSources()
                                         .FirstOrDefault(x => x.Name == "WebJobs-Extensions-DurableTask");

            if (eventSource != null)
            {
                new DurableTaskEventListener().EnableEvents(eventSource, EventLevel.Informational);

                _startWasCalled = true;
            }
        }
    }
}
