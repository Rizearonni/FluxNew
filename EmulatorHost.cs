using System.Linq;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;

namespace FluxNew
{
    public static class EmulatorHost
    {
        private static EmulatorWindow? GetEmulator()
        {
            try
            {
                var lifetime = Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime;
                if (lifetime == null) return null;
                return lifetime.Windows.OfType<EmulatorWindow>().FirstOrDefault();
            }
            catch { return null; }
        }

        public static bool SetUnitHealth(string name, double percent)
        {
            var w = GetEmulator();
            if (w == null) return false;
            return w.SetUnitHealth(name, percent);
        }

        public static bool SetUnitPower(string name, double percent)
        {
            var w = GetEmulator();
            if (w == null) return false;
            return w.SetUnitPower(name, percent);
        }
    }
}
