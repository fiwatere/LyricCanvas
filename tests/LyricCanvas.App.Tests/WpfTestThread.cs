using System.Runtime.ExceptionServices;
using System.Windows.Threading;

namespace LyricCanvas.App.Tests;

/// <summary>离线创建和测量 WPF 对象；不启动 Application、不显示任何窗口。</summary>
internal static class WpfTestThread
{
    public static void Run(Action action)
    {
        ExceptionDispatchInfo? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception exception)
            {
                failure = ExceptionDispatchInfo.Capture(exception);
            }
            finally
            {
                Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
        })
        {
            IsBackground = true,
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(30)))
        {
            throw new TimeoutException("The offline WPF measurement did not finish within 30 seconds.");
        }

        failure?.Throw();
    }
}
