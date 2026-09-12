using System.Reflection;
using System.Threading;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.Client.NoObf;

namespace VintageHorizons;

/// <summary>
/// Adds Vintage Horizons to the in-world vanilla Settings header. Vintage Story does not expose
/// a settings-page registration API in 1.22, so this is one guarded hook at the private header
/// factory. The command dialog remains available if a future game version changes that seam.
/// </summary>
internal sealed class VintageHorizonsSettingsIntegration : IDisposable
{
    internal const string HarmonyId = "vintagehorizons.settingstab";
    internal const string PageCode = "vintagehorizons";
    const string ComposerName = "gamesettings-vintagehorizons";
    const double HorizonsButtonWidth = 92;
    const double HeaderGap = 15;

    static VintageHorizonsSettingsIntegration? active;

    readonly ICoreClientAPI capi;
    readonly System.Func<Action, Action, VintageHorizonsConfigDialog> createEditor;
    readonly Harmony harmony = new(HarmonyId);
    readonly List<MethodBase> patchedMethods = new();

    MethodInfo? composerHeader;
    FieldInfo? handlerField;
    FieldInfo? mainScreenField;
    FieldInfo? backBoundsField;
    FieldInfo? composerField;
    ILogger? logger;
    GuiCompositeSettings? activeSettings;
    IGameSettingsHandler? activeHandler;
    VintageHorizonsConfigDialog? editor;
    bool installed;

    internal VintageHorizonsSettingsIntegration(ICoreClientAPI capi,
        System.Func<Action, Action, VintageHorizonsConfigDialog> createEditor)
    {
        this.capi = capi;
        this.createEditor = createEditor;
    }

    internal IReadOnlyList<MethodBase> PatchedMethods => patchedMethods;

    internal bool Install(ILogger logger)
    {
        if (installed) return true;
        this.logger = logger;

        Type settingsType = typeof(GuiCompositeSettings);
        composerHeader = AccessTools.DeclaredMethod(settingsType, "ComposerHeader",
            new[] { typeof(string), typeof(string) });
        handlerField = AccessTools.DeclaredField(settingsType, "handler");
        mainScreenField = AccessTools.DeclaredField(settingsType, "onMainscreen");
        backBoundsField = AccessTools.DeclaredField(settingsType, "backButtonBounds");
        composerField = AccessTools.DeclaredField(settingsType, "composer");

        Type? escapeType = AccessTools.TypeByName(
            "Vintagestory.Client.NoObf.GuiDialogEscapeMenu");
        MethodInfo? escapeHome = escapeType == null
            ? null
            : AccessTools.DeclaredMethod(escapeType, "EscapeMenuHome");
        MethodInfo? escapeClosed = escapeType == null
            ? null
            : AccessTools.DeclaredMethod(escapeType, "OnGuiClosed");

        if (composerHeader == null || handlerField == null || mainScreenField == null
            || backBoundsField == null || composerField == null
            || escapeHome == null || escapeClosed == null)
        {
            logger.Warning(
                "Vintage Horizons could not find the 1.22 in-game Settings seam; use .vhconfig instead.");
            return false;
        }

        if (Interlocked.CompareExchange(ref active, this, null) != null)
        {
            logger.Error(
                "Vintage Horizons found a second Settings-tab owner; use .vhconfig instead.");
            return false;
        }

        try
        {
            harmony.Patch(composerHeader, postfix: new HarmonyMethod(
                AccessTools.Method(typeof(VintageHorizonsSettingsIntegration),
                    nameof(AfterComposerHeader))));
            patchedMethods.Add(composerHeader);

            HarmonyMethod leavePatch = new(AccessTools.Method(
                typeof(VintageHorizonsSettingsIntegration), nameof(BeforeLeavingSettings)));
            harmony.Patch(escapeHome, prefix: leavePatch);
            patchedMethods.Add(escapeHome);
            harmony.Patch(escapeClosed, prefix: leavePatch);
            patchedMethods.Add(escapeClosed);
        }
        catch (Exception e)
        {
            RemoveHooks();
            Interlocked.CompareExchange(ref active, null, this);
            logger.Warning(
                "Vintage Horizons could not add its Settings tab; use .vhconfig instead: {0}",
                e.Message);
            return false;
        }

        installed = true;
        logger.Notification(
            "Vintage Horizons is available from the in-game Settings header and .vhconfig");
        return true;
    }

    static void AfterComposerHeader(
        GuiCompositeSettings __instance, string __1, ref GuiComposer __result)
    {
        Volatile.Read(ref active)?.ExtendHeader(__instance, __1, __result);
    }

    static void BeforeLeavingSettings()
    {
        Volatile.Read(ref active)?.CancelActivePage();
    }

    void ExtendHeader(GuiCompositeSettings settings, string currentTab, GuiComposer composer)
    {
        try
        {
            if ((bool)(mainScreenField?.GetValue(settings) ?? true)) return;

            if (currentTab != PageCode && ReferenceEquals(activeSettings, settings))
                CancelActivePage();

            if (backBoundsField?.GetValue(settings) is not ElementBounds backBounds
                || backBounds.ParentBounds is not ElementBounds innerBounds)
            {
                return;
            }

            // ComposerHeader reuses these field bounds for the Back element it just added.
            // Mutating the same instance moves that existing button; the new tab occupies its
            // former slot. The parent expands to the editor's width instead of clipping it.
            ElementBounds horizonsBounds = ElementBounds.Fixed(
                    backBounds.fixedX, backBounds.fixedY,
                    HorizonsButtonWidth, backBounds.fixedHeight)
                .WithFixedPadding(0, 3)
                .WithParent(innerBounds);
            backBounds.fixedX += HorizonsButtonWidth + HeaderGap;

            double neededWidth = Math.Max(
                VintageHorizonsConfigLayout.Width,
                backBounds.fixedX + backBounds.fixedWidth + 35);
            innerBounds.horizontalSizing = ElementSizing.Fixed;
            innerBounds.fixedWidth = neededWidth;
            if (innerBounds.ParentBounds != null)
            {
                innerBounds.ParentBounds.horizontalSizing = ElementSizing.Fixed;
                innerBounds.ParentBounds.fixedWidth =
                    neededWidth + 2 * GuiStyle.ElementToDialogPadding;
            }

            composer.AddToggleButton("Horizons", CairoFont.ButtonText(), on =>
            {
                if (on) QueueOpen(settings);
            }, horizonsBounds, PageCode);
            composer.GetToggleButton(PageCode).SetValue(currentTab == PageCode);
        }
        catch (Exception e)
        {
            logger?.Warning(
                "Vintage Horizons could not extend this Settings header; .vhconfig still works: {0}",
                e.Message);
        }
    }

    void QueueOpen(GuiCompositeSettings settings)
    {
        capi.Event.EnqueueMainThreadTask(() => OpenPage(settings),
            "vintagehorizons-open-settings-page");
    }

    void OpenPage(GuiCompositeSettings settings)
    {
        try
        {
            if (composerHeader == null || handlerField == null || composerField == null) return;
            if (handlerField.GetValue(settings) is not IGameSettingsHandler handler) return;

            if (!ReferenceEquals(activeSettings, settings))
            {
                CancelActivePage();
                activeSettings = settings;
                activeHandler = handler;
                editor = createEditor(RecomposeActivePage, QueueCloseActivePage);
            }

            GuiComposer composer = (GuiComposer)composerHeader.Invoke(
                settings, new object[] { ComposerName, PageCode })!;
            editor!.ComposeEmbedded(composer);
            composer = composer.EndChildElements().Compose();
            composerField.SetValue(settings, composer);
            handler.LoadComposer(composer);
            editor.CompleteEmbeddedComposition();
        }
        catch (Exception e)
        {
            logger?.Error(
                "Vintage Horizons could not compose its Settings page; returning to the pause menu: {0}",
                e.InnerException?.Message ?? e.Message);
            IGameSettingsHandler? handler = activeHandler;
            CancelActivePage();
            handler?.LeaveSettingsMenu();
        }
    }

    void RecomposeActivePage()
    {
        GuiCompositeSettings? settings = activeSettings;
        if (settings != null && editor != null) OpenPage(settings);
    }

    void QueueCloseActivePage()
    {
        capi.Event.EnqueueMainThreadTask(FinishActivePage,
            "vintagehorizons-close-settings-page");
    }

    void FinishActivePage()
    {
        IGameSettingsHandler? handler = activeHandler;
        editor = null;
        activeSettings = null;
        activeHandler = null;
        handler?.LeaveSettingsMenu();
    }

    internal void CancelActivePage()
    {
        VintageHorizonsConfigDialog? oldEditor = editor;
        editor = null;
        activeSettings = null;
        activeHandler = null;
        oldEditor?.CloseEmbedded();
    }

    public void Dispose()
    {
        Interlocked.CompareExchange(ref active, null, this);
        CancelActivePage();
        RemoveHooks();
        installed = false;
    }

    void RemoveHooks()
    {
        foreach (MethodBase method in patchedMethods)
        {
            try
            {
                harmony.Unpatch(method, HarmonyPatchType.All, HarmonyId);
            }
            catch
            {
            }
        }
        patchedMethods.Clear();
    }
}
