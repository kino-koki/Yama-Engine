using Microsoft.UI.Xaml;

namespace ShanyuEditor
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
