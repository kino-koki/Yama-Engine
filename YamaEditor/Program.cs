using Microsoft.UI.Xaml;

namespace YamaEditor
{
    public static class Program
    {
        [System.STAThread]
        public static void Main(string[] args)
        {
            // 将 Debug.WriteLine 输出路由到 stdout，便于外部日志采集
            System.Diagnostics.Trace.Listeners.Add(new System.Diagnostics.ConsoleTraceListener());
            WinRT.ComWrappersSupport.InitializeComWrappers();
            Application.Start(_ => new App());
        }
    }
}
