using Microsoft.UI.Xaml;

namespace YamaEditor
{
    public static class Program
    {
        [System.STAThread]
        public static void Main(string[] args)
        {
            WinRT.ComWrappersSupport.InitializeComWrappers();
            Application.Start(_ => new App());
        }
    }
}
