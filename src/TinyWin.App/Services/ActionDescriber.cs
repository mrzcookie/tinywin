using TinyWin.Catalog.Models;
using TinyWin.Core.Pipeline;

namespace TinyWin.App.Services;

/// <summary>
/// Turns a catalog action into the sentence the Review page shows.
/// </summary>
/// <remarks>
/// Presentation only. The wording is deliberately concrete — "Remove provisioned app
/// Microsoft.XboxApp", not "Clean up gaming" — because the Review page's whole job is to let
/// someone see exactly what is about to happen to their image.
/// </remarks>
public static class ActionDescriber
{
    /// <summary>
    /// One line per real operation. A package list is expanded rather than summarised, so the count
    /// on screen matches the count in the build report.
    /// </summary>
    public static IEnumerable<string> Describe(ComponentAction action)
    {
        ArgumentNullException.ThrowIfNull(action);

        switch (action.Type)
        {
            case ActionType.RemoveProvisionedAppx:
                foreach (var package in action.Packages)
                {
                    yield return $"Remove provisioned app  {package}";
                }

                break;

            case ActionType.RemoveCapability:
                yield return $"Remove capability  {action.Name}";
                break;

            case ActionType.DisableFeature:
                yield return $"Disable optional feature  {action.Name}";
                break;

            case ActionType.RemovePackage:
                yield return $"Remove package  {action.Name}";
                break;

            case ActionType.SetRegistry:
                yield return $"Set  {action.Hive}\\{action.Key}\\{Display(action.ValueName)} = {Display(action.Data?.ToString())}";
                break;

            case ActionType.DeleteRegistryKey:
                yield return $"Delete key  {action.Hive}\\{action.Key}";
                break;

            case ActionType.DeleteFile:
                yield return $"Delete file  {action.Path}";
                break;

            case ActionType.DeleteDirectory:
                yield return $"Delete folder  {action.Path}";
                break;

            case ActionType.RemoveScheduledTask:
                yield return $"Remove scheduled task  {action.Name}";
                break;

            case ActionType.DisableService:
                yield return $"Disable service  {action.Name}  (start type {action.StartType ?? 4})";
                break;

            case ActionType.TakeOwnership:
                yield return $"Take ownership of  {action.Path}";
                break;

            default:
                yield return action.Type.ToString();
                break;
        }
    }

    /// <summary>Which pipeline stage an action runs in — the grouping the Review page uses.</summary>
    public static BuildStageId StageOf(ActionType type) => type switch
    {
        ActionType.SetRegistry or ActionType.DeleteRegistryKey => BuildStageId.ApplyRegistry,
        _ => BuildStageId.ApplyComponents,
    };

    public static string StageTitle(BuildStageId stage) => stage switch
    {
        BuildStageId.Preflight => "Preflight",
        BuildStageId.InspectIso => "Inspect ISO",
        BuildStageId.StageFiles => "Stage files",
        BuildStageId.NormalizeImage => "Normalise image",
        BuildStageId.MountImage => "Mount image",
        BuildStageId.ApplyComponents => "Apply components",
        BuildStageId.ApplyRegistry => "Offline registry",
        BuildStageId.WriteUnattend => "Write autounattend.xml",
        BuildStageId.CleanupImage => "Component cleanup",
        BuildStageId.CommitImage => "Commit image",
        BuildStageId.RecompressImage => "Recompress image",
        BuildStageId.PatchBootWim => "Patch boot.wim",
        BuildStageId.BuildIso => "Build ISO",
        BuildStageId.Verify => "Verify & report",
        _ => stage.ToString(),
    };

    private static string Display(string? value) => string.IsNullOrEmpty(value) ? "(default)" : value;
}
