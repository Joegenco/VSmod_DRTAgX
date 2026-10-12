using System;
using System.Globalization;
using Newtonsoft.Json.Linq;

// Config folders intentionally share the mod's root namespace.
// ReSharper disable CheckNamespace
namespace DRTAgX;

/// <summary>Repairs invalid dependency numbers without resetting valid user choices.</summary>
internal static class ConfigNumericSafety
{
    internal static bool Repair(JObject saved, JObject defaults)
    {
        var changed = false;
        foreach (var property in defaults.Properties())
        {
            var value = saved[property.Name];
            if (value == null) continue; // Patched constructors supply omitted properties.
            if (property.Value is JObject childDefaults)
            {
                if (value is JObject child) changed |= Repair(child, childDefaults);
                else if (value.Type == JTokenType.Null)
                {
                    // Native Normalize fills sections, but not nullable tint
                    // vectors. Restore the complete authored value before use.
                    saved[property.Name] = childDefaults.DeepClone();
                    changed = true;
                }
            }
            else if (property.Value.Type is JTokenType.Integer or JTokenType.Float && value is JValue scalar &&
                     double.TryParse(scalar.ToString(CultureInfo.InvariantCulture), NumberStyles.Float,
                         CultureInfo.InvariantCulture,
                         out var number) && (!double.IsFinite(number) || Math.Abs(number) > float.MaxValue))
            {
                // Use the same culture for formatting and parsing: some current
                // cultures spell numeric Infinity as the infinity glyph. GLSL
                // must not receive nonfinite values; preserve valid settings.
                saved[property.Name] = property.Value.DeepClone();
                changed = true;
            }
        }

        return changed;
    }
}