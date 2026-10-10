using System;
using System.Globalization;

namespace Dolphin2
{
    // Development hook: search parameters can be overridden with DOLPHIN_P_<NAME> environment variables
    // so that variants can be A/B tested without separate builds. Defaults are the tested values.
    public static class Tune
    {
        public static double Get(string name, double def)
        {
            string v = Environment.GetEnvironmentVariable("DOLPHIN_P_" + name);
            return v != null && double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out double d) ? d : def;
        }
    }
}
