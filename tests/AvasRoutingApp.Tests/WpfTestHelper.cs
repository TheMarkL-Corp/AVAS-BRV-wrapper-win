using System;
using System.Threading;
using System.Windows;
using System.Windows.Threading;

namespace AvasRoutingApp.Tests
{
    /// <summary>
    /// Provides a single, persistent STA thread with an active WPF Dispatcher and Application
    /// instance for unit tests that construct WPF UI elements, windows, and WebView2 instances.
    /// 
    /// WPF only permits a single Application instance per AppDomain, and associating that
    /// instance with temporary STA threads causes subsequent tests to fail or deadlock
    /// against an orphaned dispatcher. Running all STA tests on this persistent background
    /// thread ensures 100% isolation, proper dispatcher message pumping, and clean termination.
    /// </summary>
    public static class WpfTestHelper
    {
        private static readonly Dispatcher _dispatcher;

        static WpfTestHelper()
        {
            var readyEvent = new ManualResetEventSlim(false);
            var thread = new Thread(() =>
            {
                if (Application.Current == null)
                {
                    new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                }
                Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
                readyEvent.Set();
                Dispatcher.Run();
            })
            {
                IsBackground = true,
                Name = "WpfTestStaDispatcherThread"
            };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            readyEvent.Wait();
            _dispatcher = Dispatcher.FromThread(thread)!;
        }

        public static void Run(Action action, int timeoutMs = 20000)
        {
            Run(_ => action(), timeoutMs);
        }

        public static void Run(Action<Dispatcher> action, int timeoutMs = 20000)
        {
            Exception? caught = null;
            var op = _dispatcher.InvokeAsync(() =>
            {
                try
                {
                    action(_dispatcher);
                }
                catch (Exception ex)
                {
                    caught = ex;
                }
            });

            var task = op.Task;
            if (!task.Wait(timeoutMs))
            {
                throw new TimeoutException($"STA test execution timed out after {timeoutMs}ms.");
            }

            if (caught != null)
            {
                throw new AggregateException("STA test failed", caught);
            }
        }
    }
}
