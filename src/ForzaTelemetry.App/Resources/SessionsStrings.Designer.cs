//------------------------------------------------------------------------------
// Hand-maintained strongly typed accessor for SessionsStrings.resx (same shape as InsightsStrings).
// It is a separate resource set so the Sessions page does not touch Strings.resx. Add a property
// here whenever a key is added to SessionsStrings.resx (and to SessionsStrings.es.resx).
//------------------------------------------------------------------------------

#nullable enable

namespace ForzaTelemetry.App.Resources;

/// <summary>Localized text of the Sessions page (neutral English; Spanish in SessionsStrings.es.resx).</summary>
public static class SessionsStrings
{
    private static global::System.Resources.ResourceManager? _resourceManager;

    /// <summary>The resource manager for <c>SessionsStrings.resx</c> and its satellite cultures.</summary>
    public static global::System.Resources.ResourceManager ResourceManager =>
        _resourceManager ??= new global::System.Resources.ResourceManager(
            "ForzaTelemetry.App.Resources.SessionsStrings", typeof(SessionsStrings).Assembly);

    /// <summary>Culture override for lookups; null uses the current UI culture (as set by LanguageService).</summary>
    public static global::System.Globalization.CultureInfo? Culture { get; set; }

    /// <summary>"Record"</summary>
    public static string SessionsRecordTitle => ResourceManager.GetString("SessionsRecordTitle", Culture)!;

    /// <summary>"Saves every telemetry packet to a file, so you can replay the session later, even without the game running."</summary>
    public static string SessionsRecordHint => ResourceManager.GetString("SessionsRecordHint", Culture)!;

    /// <summary>"Record"</summary>
    public static string SessionsRecordButton => ResourceManager.GetString("SessionsRecordButton", Culture)!;

    /// <summary>"Stop recording"</summary>
    public static string SessionsStopRecordingButton => ResourceManager.GetString("SessionsStopRecordingButton", Culture)!;

    /// <summary>"Save the incoming telemetry to a file"</summary>
    public static string SessionsRecordTooltip => ResourceManager.GetString("SessionsRecordTooltip", Culture)!;

    /// <summary>"Stop and save the recording"</summary>
    public static string SessionsStopRecordingTooltip => ResourceManager.GetString("SessionsStopRecordingTooltip", Culture)!;

    /// <summary>"Start listening first: there is nothing to record yet."</summary>
    public static string SessionsRecordNeedsLiveTooltip => ResourceManager.GetString("SessionsRecordNeedsLiveTooltip", Culture)!;

    /// <summary>"Stop the replay to record a new session."</summary>
    public static string SessionsRecordDuringReplayTooltip => ResourceManager.GetString("SessionsRecordDuringReplayTooltip", Culture)!;

    /// <summary>"Wait a moment: the previous action is still finishing."</summary>
    public static string SessionsRecordBusyTooltip => ResourceManager.GetString("SessionsRecordBusyTooltip", Culture)!;

    /// <summary>"Duration"</summary>
    public static string SessionsStatDuration => ResourceManager.GetString("SessionsStatDuration", Culture)!;

    /// <summary>"Size"</summary>
    public static string SessionsStatSize => ResourceManager.GetString("SessionsStatSize", Culture)!;

    /// <summary>"Packets"</summary>
    public static string SessionsStatPackets => ResourceManager.GetString("SessionsStatPackets", Culture)!;

    /// <summary>"Recording {0}"</summary>
    public static string SessionsRecordingPill => ResourceManager.GetString("SessionsRecordingPill", Culture)!;

    /// <summary>"{0} packets could not be saved: the disk is not keeping up."</summary>
    public static string SessionsDroppedWarning => ResourceManager.GetString("SessionsDroppedWarning", Culture)!;

    /// <summary>"The recording stopped writing: {0}"</summary>
    public static string SessionsRecordWriteError => ResourceManager.GetString("SessionsRecordWriteError", Culture)!;

    /// <summary>"Could not start the recording: {0}"</summary>
    public static string SessionsRecordStartFailed => ResourceManager.GetString("SessionsRecordStartFailed", Culture)!;

    /// <summary>"Saved as {0}"</summary>
    public static string SessionsSavedNotice => ResourceManager.GetString("SessionsSavedNotice", Culture)!;

    /// <summary>"Recording saved as {0}. Listening stopped, so recording ended with it."</summary>
    public static string SessionsRecordingEndedNotice => ResourceManager.GetString("SessionsRecordingEndedNotice", Culture)!;

    /// <summary>"Recordings"</summary>
    public static string SessionsLibraryTitle => ResourceManager.GetString("SessionsLibraryTitle", Culture)!;

    /// <summary>"Open folder"</summary>
    public static string SessionsOpenFolder => ResourceManager.GetString("SessionsOpenFolder", Culture)!;

    /// <summary>"Show the recordings folder in Explorer"</summary>
    public static string SessionsOpenFolderTooltip => ResourceManager.GetString("SessionsOpenFolderTooltip", Culture)!;

    /// <summary>"Loading recordings..."</summary>
    public static string SessionsLoading => ResourceManager.GetString("SessionsLoading", Culture)!;

    /// <summary>"Record a session to replay it later — even without the game running."</summary>
    public static string SessionsEmpty => ResourceManager.GetString("SessionsEmpty", Culture)!;

    /// <summary>"Recording"</summary>
    public static string SessionsColRecording => ResourceManager.GetString("SessionsColRecording", Culture)!;

    /// <summary>"Duration"</summary>
    public static string SessionsColDuration => ResourceManager.GetString("SessionsColDuration", Culture)!;

    /// <summary>"Size"</summary>
    public static string SessionsColSize => ResourceManager.GetString("SessionsColSize", Culture)!;

    /// <summary>"Cars"</summary>
    public static string SessionsColCars => ResourceManager.GetString("SessionsColCars", Culture)!;

    /// <summary>"Incomplete"</summary>
    public static string SessionsTruncated => ResourceManager.GetString("SessionsTruncated", Culture)!;

    /// <summary>"The app closed while this was being recorded. Everything saved before that still plays."</summary>
    public static string SessionsTruncatedTooltip => ResourceManager.GetString("SessionsTruncatedTooltip", Culture)!;

    /// <summary>"Unreadable"</summary>
    public static string SessionsUnreadable => ResourceManager.GetString("SessionsUnreadable", Culture)!;

    /// <summary>"This file is not a recording this version can read."</summary>
    public static string SessionsUnreadableTooltip => ResourceManager.GetString("SessionsUnreadableTooltip", Culture)!;

    /// <summary>"No race"</summary>
    public static string SessionsNoCars => ResourceManager.GetString("SessionsNoCars", Culture)!;

    /// <summary>"{0} +{1} more"</summary>
    public static string SessionsCarsMoreFormat => ResourceManager.GetString("SessionsCarsMoreFormat", Culture)!;

    /// <summary>"Play"</summary>
    public static string SessionsPlay => ResourceManager.GetString("SessionsPlay", Culture)!;

    /// <summary>"Play {0}"</summary>
    public static string SessionsPlayNamed => ResourceManager.GetString("SessionsPlayNamed", Culture)!;

    /// <summary>"Rename"</summary>
    public static string SessionsRename => ResourceManager.GetString("SessionsRename", Culture)!;

    /// <summary>"Rename {0}"</summary>
    public static string SessionsRenameNamed => ResourceManager.GetString("SessionsRenameNamed", Culture)!;

    /// <summary>"Delete"</summary>
    public static string SessionsDelete => ResourceManager.GetString("SessionsDelete", Culture)!;

    /// <summary>"Delete {0}"</summary>
    public static string SessionsDeleteNamed => ResourceManager.GetString("SessionsDeleteNamed", Culture)!;

    /// <summary>"It is playing now: stop the replay first."</summary>
    public static string SessionsInUseTooltip => ResourceManager.GetString("SessionsInUseTooltip", Culture)!;

    /// <summary>"It is being exported to CSV: wait for it to finish."</summary>
    public static string SessionsExportingTooltip => ResourceManager.GetString("SessionsExportingTooltip", Culture)!;

    /// <summary>"{0}, {1}, {2}, {3}"</summary>
    public static string SessionsRowName => ResourceManager.GetString("SessionsRowName", Culture)!;

    /// <summary>"New name for {0}"</summary>
    public static string SessionsNameBoxName => ResourceManager.GetString("SessionsNameBoxName", Culture)!;

    /// <summary>"Enter to save, Esc to cancel"</summary>
    public static string SessionsRenameHint => ResourceManager.GetString("SessionsRenameHint", Culture)!;

    /// <summary>"Type a name."</summary>
    public static string SessionsNameEmpty => ResourceManager.GetString("SessionsNameEmpty", Culture)!;

    /// <summary>"A name cannot contain \ / : * ? ' &lt; &gt; |"</summary>
    public static string SessionsNameInvalid => ResourceManager.GetString("SessionsNameInvalid", Culture)!;

    /// <summary>"Windows reserves that name."</summary>
    public static string SessionsNameReserved => ResourceManager.GetString("SessionsNameReserved", Culture)!;

    /// <summary>"A name cannot start with a space or end with a space or a dot."</summary>
    public static string SessionsNameTrailing => ResourceManager.GetString("SessionsNameTrailing", Culture)!;

    /// <summary>"That name is too long (100 characters at most)."</summary>
    public static string SessionsNameTooLong => ResourceManager.GetString("SessionsNameTooLong", Culture)!;

    /// <summary>"A recording with that name already exists."</summary>
    public static string SessionsNameExists => ResourceManager.GetString("SessionsNameExists", Culture)!;

    /// <summary>"Could not rename the file. It may be in use."</summary>
    public static string SessionsRenameFailed => ResourceManager.GetString("SessionsRenameFailed", Culture)!;

    /// <summary>"Delete this recording?"</summary>
    public static string SessionsDeleteConfirmTitle => ResourceManager.GetString("SessionsDeleteConfirmTitle", Culture)!;

    /// <summary>"'{0}' will be deleted permanently. This cannot be undone."</summary>
    public static string SessionsDeleteConfirmMessage => ResourceManager.GetString("SessionsDeleteConfirmMessage", Culture)!;

    /// <summary>"Delete recording"</summary>
    public static string SessionsDeleteConfirmButton => ResourceManager.GetString("SessionsDeleteConfirmButton", Culture)!;

    /// <summary>"Could not delete {0}. It may be in use."</summary>
    public static string SessionsDeleteFailed => ResourceManager.GetString("SessionsDeleteFailed", Culture)!;

    /// <summary>"Replay"</summary>
    public static string SessionsPlayerTitle => ResourceManager.GetString("SessionsPlayerTitle", Culture)!;

    /// <summary>"Every tab is showing this recording instead of live data."</summary>
    public static string SessionsPlayerHint => ResourceManager.GetString("SessionsPlayerHint", Culture)!;

    /// <summary>"Pause"</summary>
    public static string SessionsPause => ResourceManager.GetString("SessionsPause", Culture)!;

    /// <summary>"Resume"</summary>
    public static string SessionsResume => ResourceManager.GetString("SessionsResume", Culture)!;

    /// <summary>"Replay from the start"</summary>
    public static string SessionsReplayAgain => ResourceManager.GetString("SessionsReplayAgain", Culture)!;

    /// <summary>"Restart"</summary>
    public static string SessionsRestart => ResourceManager.GetString("SessionsRestart", Culture)!;

    /// <summary>"Back to the start of the recording"</summary>
    public static string SessionsRestartTooltip => ResourceManager.GetString("SessionsRestartTooltip", Culture)!;

    /// <summary>"Stop replay"</summary>
    public static string SessionsStopReplay => ResourceManager.GetString("SessionsStopReplay", Culture)!;

    /// <summary>"Leave replay mode (press Start to listen to the game again)"</summary>
    public static string SessionsStopReplayTooltip => ResourceManager.GetString("SessionsStopReplayTooltip", Culture)!;

    /// <summary>"Speed"</summary>
    public static string SessionsSpeedLabel => ResourceManager.GetString("SessionsSpeedLabel", Culture)!;

    /// <summary>"0.5×"</summary>
    public static string SessionsSpeedHalf => ResourceManager.GetString("SessionsSpeedHalf", Culture)!;

    /// <summary>"1×"</summary>
    public static string SessionsSpeedNormal => ResourceManager.GetString("SessionsSpeedNormal", Culture)!;

    /// <summary>"2×"</summary>
    public static string SessionsSpeedDouble => ResourceManager.GetString("SessionsSpeedDouble", Culture)!;

    /// <summary>"4×"</summary>
    public static string SessionsSpeedQuadruple => ResourceManager.GetString("SessionsSpeedQuadruple", Culture)!;

    /// <summary>"Playback speed"</summary>
    public static string SessionsSpeedName => ResourceManager.GetString("SessionsSpeedName", Culture)!;

    /// <summary>"Finished"</summary>
    public static string SessionsFinished => ResourceManager.GetString("SessionsFinished", Culture)!;

    /// <summary>"Paused"</summary>
    public static string SessionsPaused => ResourceManager.GetString("SessionsPaused", Culture)!;

    /// <summary>"Playing"</summary>
    public static string SessionsPlaying => ResourceManager.GetString("SessionsPlaying", Culture)!;

    /// <summary>"Playback position"</summary>
    public static string SessionsProgressName => ResourceManager.GetString("SessionsProgressName", Culture)!;

    /// <summary>"{0} / {1}"</summary>
    public static string SessionsPositionFormat => ResourceManager.GetString("SessionsPositionFormat", Culture)!;

    /// <summary>"Could not play {0}: {1}"</summary>
    public static string SessionsPlaybackFailed => ResourceManager.GetString("SessionsPlaybackFailed", Culture)!;
}
