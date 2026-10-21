using System.Text.Json;
using System.Text.Json.Nodes;

namespace Tenantry.Core.Changes;

/// <summary>A tenant's Test mode switch, in settings.json in the tenant's own app-data folder.</summary>
public sealed class JsonTestModeStore(string tenantFolder, bool defaultOn) : ITestModeStore
{
    string Path => System.IO.Path.Combine(tenantFolder, "settings.json");

    public bool IsTestMode
    {
        get
        {
            try
            {
                return File.Exists(Path) && JsonNode.Parse(File.ReadAllText(Path))?["testMode"] is JsonValue v && v.TryGetValue(out bool on)
                    ? on
                    : defaultOn;
            }
            catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
            {
                // Unreadable settings fail safe: Test mode on.
                return true;
            }
        }
    }

    public void Set(bool testMode)
    {
        System.IO.Directory.CreateDirectory(tenantFolder);
        File.WriteAllText(Path, new JsonObject { ["testMode"] = testMode }.ToJsonString());
    }
}
