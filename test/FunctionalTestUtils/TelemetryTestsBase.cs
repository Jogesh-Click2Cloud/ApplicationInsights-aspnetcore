﻿using System.Threading.Tasks;

namespace FunctionalTestUtils
{
    using System;
    using System.Diagnostics;
    using System.Linq;
    using System.Net.Http;
    using System.Reflection;
    using System.Threading;
    using Microsoft.ApplicationInsights.DataContracts;
    using Microsoft.Extensions.DependencyInjection;
    using Xunit;
#if NET451
    using System.Net;
    using Microsoft.ApplicationInsights.Extensibility.PerfCounterCollector;
    using Microsoft.ApplicationInsights.Extensibility;
#endif

    public abstract class TelemetryTestsBase
    {
        protected const int TestTimeoutMs = 10000;

        public void ValidateBasicRequest(InProcessServer server, string requestPath, RequestTelemetry expected)
        {
            DateTimeOffset testStart = DateTimeOffset.Now;
            var timer = Stopwatch.StartNew();

            var httpClientHandler = new HttpClientHandler();
            httpClientHandler.UseDefaultCredentials = true;

            Task<HttpResponseMessage> task;
            using (var httpClient = new HttpClient(httpClientHandler, true))
            {
                task = httpClient.GetAsync(server.BaseHost + requestPath);
                task.Wait(TestTimeoutMs);
            }
            var result = task.Result;

            timer.Stop();
            server.Dispose();

            var actual = server.BackChannel.Buffer.OfType<RequestTelemetry>().Single();

            Assert.Equal(expected.ResponseCode, actual.ResponseCode);
            Assert.Equal(expected.Name, actual.Name);
            Assert.Equal(expected.Success, actual.Success);
            Assert.Equal(expected.Url, actual.Url);
            Assert.InRange(actual.Timestamp, testStart, DateTimeOffset.Now);
            Assert.True(actual.Duration < timer.Elapsed, "duration");
            Assert.Equal(expected.HttpMethod, actual.HttpMethod);
        }

        public void ValidateBasicException(InProcessServer server, string requestPath, ExceptionTelemetry expected)
        {
            DateTimeOffset testStart = DateTimeOffset.Now;
            var httpClientHandler = new HttpClientHandler();
            httpClientHandler.UseDefaultCredentials = true;

            Task<HttpResponseMessage> task;
            using (var httpClient = new HttpClient(httpClientHandler, true))
            {
                task = httpClient.GetAsync(server.BaseHost + requestPath);
                task.Wait(TestTimeoutMs);
            }
            var result = task.Result;
            server.Dispose();
            var actual = server.BackChannel.Buffer.OfType<ExceptionTelemetry>().Single();

            Assert.Equal(expected.Exception.GetType(), actual.Exception.GetType());
            Assert.NotEmpty(actual.Exception.StackTrace);
            Assert.Equal(actual.HandledAt, actual.HandledAt);
            Assert.NotEmpty(actual.Context.Operation.Name);
            Assert.NotEmpty(actual.Context.Operation.Id);
        }

#if NET451
        public void ValidateBasicDependency(string assemblyName, string requestPath)
        {
            DependencyTelemetry expected = new DependencyTelemetry();
            expected.ResultCode = "200";
            expected.Success = true;
            expected.Name = requestPath;

            InProcessServer server;
            using (server = new InProcessServer(assemblyName))
            {
                expected.Data = server.BaseHost + requestPath;

                var timer = Stopwatch.StartNew();
                Task<HttpResponseMessage> task;
                using (var httpClient = new HttpClient())
                {
                    task = httpClient.GetAsync(server.BaseHost + requestPath);
                    task.Wait(TestTimeoutMs);
                }
                var result = task.Result;
                timer.Stop();
            }

            Assert.Contains(server.BackChannel.Buffer.OfType<DependencyTelemetry>(),
                d => d.Name == expected.Name
                  && d.Data == expected.Data
                  && d.Success == expected.Success
                  && d.ResultCode == expected.ResultCode
                );
        }

        public void ValidatePerformanceCountersAreCollected(string assemblyName)
        {
            using (var server = new InProcessServer(assemblyName))
            {
                // Reconfigure the PerformanceCollectorModule timer.
                Type perfModuleType = typeof(PerformanceCollectorModule);
                PerformanceCollectorModule perfModule = (PerformanceCollectorModule)server.ApplicationServices.GetServices<ITelemetryModule>().FirstOrDefault(m => m.GetType() == perfModuleType);
                FieldInfo timerField = perfModuleType.GetField("timer", BindingFlags.NonPublic | BindingFlags.Instance);
                var timer = timerField.GetValue(perfModule);
                timerField.FieldType.InvokeMember("ScheduleNextTick", BindingFlags.InvokeMethod | BindingFlags.Public | BindingFlags.Instance, null, timer, new object[] { TimeSpan.FromMilliseconds(10) });

                DateTime timeout = DateTime.Now.AddSeconds(10);
                int numberOfCountersSent = 0;
                do
                {
                    Thread.Sleep(1000);
                    numberOfCountersSent = server.BackChannel.Buffer.OfType<PerformanceCounterTelemetry>().Distinct().Count();
                } while (numberOfCountersSent == 0 && DateTime.Now < timeout);

                Assert.True(numberOfCountersSent > 0);
            }
        }
#endif
    }
}