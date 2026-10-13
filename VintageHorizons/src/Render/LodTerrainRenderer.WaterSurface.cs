using Vintagestory.API.Client;

namespace VintageHorizons;

public partial class LodTerrainRenderer
{
    IShaderProgram? waterSurfaceProg;
    bool waterSurfaceShaderOk;
    bool waterSurfaceFailed;
    bool waterPipelineFailedThisFrame;
    string? reportedRequiredWaterFailure;

    // Kept as a status property for captures and commands written against earlier builds.
    // The legacy shader choice is retired and there is no setter that can re-enable it.
    public bool DedicatedWaterShader => true;

    public string WaterShaderStatus => waterSurfaceFailed
        ? "FAILED (dedicated draw failed; reload shaders to retry)"
        : !waterSurfaceShaderOk || waterSurfaceProg == null || waterSurfaceProg.LoadError
            ? "FAILED (dedicated shader unavailable)" : "dedicated";

    void LoadWaterSurfaceShader()
    {
        waterSurfaceProg = capi.Shader.NewShaderProgram();
        waterSurfaceProg.AssetDomain = "dodenslandscapes";
        waterSurfaceProg.VertexShader = capi.Shader.NewShader(EnumShaderType.VertexShader);
        waterSurfaceProg.FragmentShader = capi.Shader.NewShader(EnumShaderType.FragmentShader);
        capi.Shader.RegisterFileShaderProgram("lodwater", waterSurfaceProg);
        waterSurfaceShaderOk = waterSurfaceProg.Compile();
        if (waterSurfaceShaderOk) LodTerrainSamplerLayout.Configure(waterSurfaceProg);
        waterSurfaceFailed = false;
        if (waterSurfaceShaderOk)
            capi.Logger.Notification(
                "[Landscapes] dedicated water surface shader ready; legacy shader retired.");
        else
            capi.Logger.Error(
                "[Landscapes] dedicated opaque water shader failed to compile; "
                + "selecting the opaque pipeline will suppress cached water.");
    }

    void DrawWaterSurface()
    {
        if (waterFrame.UseOit || waterPipelineFailedThisFrame || waterMeshes.Count == 0) return;
        if (!RequiredWaterDepthCompleted(out string depthFailure))
        {
            ReportRequiredWaterFailure(depthFailure);
            return;
        }
        if (!waterSurfaceShaderOk || waterSurfaceFailed
            || waterSurfaceProg == null || waterSurfaceProg.LoadError)
        {
            ReportRequiredWaterFailure("the dedicated opaque water shader is unavailable");
            return;
        }

        IShaderProgram previous = prog!;
        previous.Stop();
        try
        {
            prog = waterSurfaceProg;
            prog.Use();
            ApplyFrameUniforms(prog, ApprovedViewDistance());
            // Thin cover shares this CPU buffer. The dedicated program deliberately
            // preserves band 2 shading; it must not discard everything except water.
            DrawWaterMeshes();
            ClearRequiredWaterFailure();
        }
        catch (Exception e)
        {
            waterSurfaceFailed = true;
            ReportRequiredWaterFailure("dedicated opaque water draw failed: " + e.Message);
        }
        finally
        {
            waterSurfaceProg.Stop();
            prog = previous;
            previous.Use();
            RebindOwnSamplers(previous);
        }
    }

    bool WaterDepthRequired => WaterGroundMurk && WaterDepthCoverage && WaterSurfaceVisible
        && waterMeshes.Count > 0;

    bool RequiredWaterDepthPrerequisitesAvailable(out string failure)
    {
        failure = "";
        if (!WaterDepthRequired) return true;
        if (!waterDepthShaderOk || waterDepthFailed || waterDepthProg == null
            || waterDepthProg.LoadError)
            failure = "the required cached water-depth shader/pass is unavailable";
        else if (WaterDepthRepair && (!waterRepairShaderOk || waterRepairFailed
            || waterRepairProg == null || waterRepairProg.LoadError))
            failure = "the requested water-depth repair shader/pass is unavailable";
        else if (WaterJunctionDepth && waterJunctionFailed)
            failure = "the requested water junction-depth snapshot failed";
        return failure.Length == 0;
    }

    bool RequiredWaterDepthCompleted(out string failure)
    {
        if (!RequiredWaterDepthPrerequisitesAvailable(out failure)) return false;
        if (!WaterDepthRequired || waterDepthCompletedThisFrame) return true;
        failure = "the required cached water-depth pass did not complete this frame";
        return false;
    }

    void ReportRequiredWaterFailure(string reason)
    {
        WaterPipelineStatus = "FAILED: " + reason;
        if (reportedRequiredWaterFailure == reason) return;
        reportedRequiredWaterFailure = reason;
        capi.Logger.Error(
            "[Landscapes] REQUIRED WATER PATH FAILED; cached water/thin cover is "
            + "suppressed and no legacy/opaque fallback will run: {0}.", reason);
    }

    void ClearRequiredWaterFailure() => reportedRequiredWaterFailure = null;
}
