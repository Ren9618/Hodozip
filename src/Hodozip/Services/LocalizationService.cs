using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Windows.ApplicationModel.Resources;

namespace Hodozip.Services;

/// <summary>アプリの表示言語。System は OS の UI 言語に従う。</summary>
public enum AppLanguage
{
    System,
    Japanese,
    English,
}

/// <summary>
/// .resw リソースを読み込む。言語は起動時に一度だけ決まり、変更は次回起動時に反映する。
/// </summary>
internal static class LocalizationService
{
    private static ResourceManager? _resourceManager;
    private static ResourceContext? _resourceContext;

    public static string CurrentLanguage { get; private set; } = "en-US";

    public static bool IsJapanese => CurrentLanguage.StartsWith("ja", StringComparison.OrdinalIgnoreCase);

    public static void Initialize(AppLanguage language)
    {
        CurrentLanguage = ResolveLanguage(language);
        try
        {
            _resourceManager = new ResourceManager("Hodozip.pri");
            _resourceContext = _resourceManager.CreateResourceContext();
            _resourceContext.QualifierValues["Language"] = CurrentLanguage;
        }
        catch
        {
            _resourceManager = null;
            _resourceContext = null;
        }
    }

    public static string Get(string key)
    {
        if (_resourceManager is null)
        {
            return key;
        }

        try
        {
            // Strings/<language>/Resources.resw は PRI 上では Resources/<key> になる。
            return _resourceManager.MainResourceMap.TryGetValue("Resources/" + key, _resourceContext)?.ValueAsString
                ?? _resourceManager.MainResourceMap.TryGetValue(key, _resourceContext)?.ValueAsString
                ?? key;
        }
        catch
        {
            return key;
        }
    }

    public static string Format(string key, params object?[] arguments)
    {
        var format = Get(key);
        try
        {
            return string.Format(CultureInfo.CurrentCulture, format, arguments);
        }
        catch (FormatException)
        {
            return format;
        }
    }

    public static string ResolveLanguage(AppLanguage language)
    {
        return language switch
        {
            AppLanguage.Japanese => "ja-JP",
            AppLanguage.English => "en-US",
            _ => GetSystemUiLanguage(),
        };
    }

    private static string GetSystemUiLanguage()
    {
        try
        {
            var primaryLanguageId = GetUserDefaultUILanguage() & 0x3FF;
            return primaryLanguageId == 0x11 ? "ja-JP" : "en-US";
        }
        catch
        {
            return CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.Equals("ja", StringComparison.OrdinalIgnoreCase)
                ? "ja-JP"
                : "en-US";
        }
    }

    [DllImport("kernel32.dll")]
    private static extern ushort GetUserDefaultUILanguage();

    public static string GetIssueMessage(Hodozip.Core.ExtractionIssue issue)
    {
        if (!string.IsNullOrEmpty(issue.MessageKey))
        {
            var arguments = issue.MessageArguments.Count == 0
                ? Array.Empty<object?>()
                : issue.MessageArguments.ToArray();
            var localized = Format(issue.MessageKey!, arguments);
            if (!string.Equals(localized, issue.MessageKey, StringComparison.Ordinal))
            {
                return localized;
            }
        }

        // キーを持たない旧来のメッセージでも、英語 UI に日本語を出さない。
        if (!IsJapanese)
        {
            return issue.Kind switch
            {
                Hodozip.Core.ExtractionIssueKind.PathTraversal => Get("Issue_OutsideRoot"),
                Hodozip.Core.ExtractionIssueKind.SymbolicLink => Get("Issue_SymlinkSkipped"),
                Hodozip.Core.ExtractionIssueKind.UnsafeName => Get("Issue_UnsafeName").Replace("{0}", issue.EntryPath, StringComparison.Ordinal),
                Hodozip.Core.ExtractionIssueKind.SuspiciousExpansion => Get("Issue_ExpansionWarning").Replace("{0:N0}", "?", StringComparison.Ordinal),
                Hodozip.Core.ExtractionIssueKind.Password => Get("Issue_PasswordIncorrect"),
                Hodozip.Core.ExtractionIssueKind.Cancelled => Get("Issue_Cancelled"),
                _ => Get("Issue_Generic"),
            };
        }

        return issue.Message;
    }

    public static string GetPasswordError(Hodozip.Core.PasswordRequest request)
    {
        if (!string.IsNullOrEmpty(request.ErrorMessageKey))
        {
            var localized = Get(request.ErrorMessageKey);
            if (!string.Equals(localized, request.ErrorMessageKey, StringComparison.Ordinal))
            {
                return localized;
            }
        }

        return request.ErrorMessage ?? string.Empty;
    }

    public static string GetCompressionError(Hodozip.Core.CompressionResult result)
    {
        if (!string.IsNullOrEmpty(result.ErrorMessageKey))
        {
            var arguments = result.ErrorArguments.Count == 0
                ? Array.Empty<object?>()
                : result.ErrorArguments.ToArray();
            var localized = Format(result.ErrorMessageKey!, arguments);
            if (!string.Equals(localized, result.ErrorMessageKey, StringComparison.Ordinal))
            {
                return localized;
            }
        }

        return result.ErrorMessage ?? string.Empty;
    }
}

/// <summary>型付きのリソース アクセサ。キーの綴り間違いを 1 か所に閉じ込める。</summary>
internal static class Loc
{
    private static string T(string key) => LocalizationService.Get(key);

    private static string F(string key, params object?[] arguments) =>
        LocalizationService.Format(key, arguments);

    public static string Common_OpenFolder => T("Common_OpenFolder");
    public static string Common_Cancel => T("Common_Cancel");
    public static string Common_Close => T("Common_Close");
    public static string Common_Settings => T("Common_Settings");
    public static string Common_About => T("Common_About");
    public static string Common_Feedback => T("Common_Feedback");
    public static string Common_Support => T("Common_Support");
    public static string Common_Browse => T("Common_Browse");
    public static string Common_Remove => T("Common_Remove");

    public static string Main_Tagline => T("Main_Tagline");
    public static string Main_DropHere => T("Main_DropHere");
    public static string Main_Or => T("Main_Or");
    public static string Main_SelectFile => T("Main_SelectFile");
    public static string Main_SupportedFormats => T("Main_SupportedFormats");
    public static string Main_PreviousResult => T("Main_PreviousResult");
    public static string Main_FilePickErrorTitle => T("Main_FilePickErrorTitle");
    public static string Main_DropCaption => T("Main_DropCaption");
    public static string Main_NotArchiveTitle => T("Main_NotArchiveTitle");
    public static string Main_DropFileMessage => T("Main_DropFileMessage");
    public static string Main_UnsupportedTitle => T("Main_UnsupportedTitle");
    public static string Main_UnsupportedDropMessage => T("Main_UnsupportedDropMessage");
    public static string Main_MultipleTitle => T("Main_MultipleTitle");
    public static string Main_MultipleMessage => T("Main_MultipleMessage");
    public static string Main_ReadErrorTitle => T("Main_ReadErrorTitle");
    public static string Main_FileNotFoundTitle => T("Main_FileNotFoundTitle");
    public static string Main_UnsupportedFileMessage => T("Main_UnsupportedFileMessage");
    public static string Main_ExtractingTitle => T("Main_ExtractingTitle");
    public static string Main_ExtractingMessage => T("Main_ExtractingMessage");
    public static string Main_CompletedTitle => T("Main_CompletedTitle");
    public static string Main_CancelledTitle => T("Main_CancelledTitle");
    public static string Main_FailedTitle => T("Main_FailedTitle");
    public static string Main_ResultDestination(string path) => F("Main_ResultDestination", path);
    public static string Main_ResultFiles(int files, int skipped) => F("Main_ResultFiles", files, skipped);
    public static string Main_ResultIssues(int warnings, int errors) => F("Main_ResultIssues", warnings, errors);
    public static string Main_Compress => T("Main_Compress");
    public static string Main_UpdateAvailableTitle => T("Main_UpdateAvailableTitle");
    public static string Main_UpdateAvailableMessage(string version) => F("Main_UpdateAvailableMessage", version);
    public static string Main_UpdateNow => T("Main_UpdateNow");
    public static string Main_UpdateLater => T("Main_UpdateLater");
    public static string Main_UpdateSkip => T("Main_UpdateSkip");
    public static string Main_UpdateDownloading(int percent) => F("Main_UpdateDownloading", percent);
    public static string Main_UpdateFailedTitle => T("Main_UpdateFailedTitle");
    public static string Main_UpdatePortableMessage => T("Main_UpdatePortableMessage");
    public static string Main_CompressionCompletedTitle => T("Main_CompressionCompletedTitle");
    public static string Main_CompressionCancelledTitle => T("Main_CompressionCancelledTitle");
    public static string Main_CompressionFailedTitle => T("Main_CompressionFailedTitle");
    public static string Main_CompressionResultOutput(string path) => F("Main_CompressionResultOutput", path);
    public static string Main_CompressionResultFiles(int files, string source, string output) => F("Main_CompressionResultFiles", files, source, output);
    public static string Main_JobsTitle => T("Main_JobsTitle");
    public static string Main_JobsHint => T("Main_JobsHint");
    public static string Main_JobShow => T("Main_JobShow");
    public static string Main_JobCancel => T("Main_JobCancel");
    public static string Main_JobExtract => T("Main_JobExtract");
    public static string Main_JobCompress => T("Main_JobCompress");
    public static string Main_JobPercent(int percent) => F("Main_JobPercent", percent);

    public static string Notification_ExtractCompletedTitle => T("Notification_ExtractCompletedTitle");
    public static string Notification_ExtractFailedTitle => T("Notification_ExtractFailedTitle");
    public static string Notification_CompressionCompletedTitle => T("Notification_CompressionCompletedTitle");
    public static string Notification_CompressionFailedTitle => T("Notification_CompressionFailedTitle");
    public static string Notification_ExtractCompletedMessage(string name, int files, int skipped) => F("Notification_ExtractCompletedMessage", name, files, skipped);
    public static string Notification_ExtractFailedMessage(string name, string message) => F("Notification_ExtractFailedMessage", name, message);
    public static string Notification_CompressionCompletedMessage(string name, string output) => F("Notification_CompressionCompletedMessage", name, output);
    public static string Notification_CompressionFailedMessage(string name, string message) => F("Notification_CompressionFailedMessage", name, message);

    public static string Compression_Title => T("Compression_Title");
    public static string Compression_WindowTitle(string product) => F("Compression_WindowTitle", product);
    public static string Compression_Subtitle => T("Compression_Subtitle");
    public static string Compression_Sources => T("Compression_Sources");
    public static string Compression_AddFiles => T("Compression_AddFiles");
    public static string Compression_AddFolder => T("Compression_AddFolder");
    public static string Compression_RemoveSelected => T("Compression_RemoveSelected");
    public static string Compression_NoSources => T("Compression_NoSources");
    public static string Compression_Output => T("Compression_Output");
    public static string Compression_OutputFolder => T("Compression_OutputFolder");
    public static string Compression_OutputName => T("Compression_OutputName");
    public static string Compression_Format => T("Compression_Format");
    public static string Compression_FormatZip => T("Compression_FormatZip");
    public static string Compression_Format7z => T("Compression_Format7z");
    public static string Compression_Level => T("Compression_Level");
    public static string Compression_LevelStore => T("Compression_LevelStore");
    public static string Compression_LevelNormal => T("Compression_LevelNormal");
    public static string Compression_LevelMaximum => T("Compression_LevelMaximum");
    public static string Compression_Password => T("Compression_Password");
    public static string Compression_PasswordPlaceholder => T("Compression_PasswordPlaceholder");
    public static string Compression_PasswordNote => T("Compression_PasswordNote");
    public static string Compression_Password7zNote => T("Compression_Password7zNote");
    public static string Compression_ZipEncoding => T("Compression_ZipEncoding");
    public static string Compression_ZipEncodingUtf8 => T("Compression_ZipEncodingUtf8");
    public static string Compression_ZipEncodingShiftJis => T("Compression_ZipEncodingShiftJis");
    public static string Compression_ZipEncodingNote => T("Compression_ZipEncodingNote");
    public static string Compression_CollisionNote => T("Compression_CollisionNote");
    public static string Compression_Start => T("Compression_Start");
    public static string Compression_ErrorTitle => T("Compression_ErrorTitle");
    public static string Compression_ErrorNoSources => T("Compression_ErrorNoSources");
    public static string Compression_ErrorNoOutputName => T("Compression_ErrorNoOutputName");
    public static string Compression_ErrorInvalidName => T("Compression_ErrorInvalidName");
    public static string Compression_ErrorFolderMissing => T("Compression_ErrorFolderMissing");
    public static string Compression_ErrorFolderCreate(string path) => F("Compression_ErrorFolderCreate", path);
    public static string Compression_ErrorSourceMissing(string path) => F("Compression_ErrorSourceMissing", path);
    public static string Compression_ErrorUnexpected(string message) => F("Compression_ErrorUnexpected", message);
    public static string Compression_SourceMissing(string path) => F("Compression_SourceMissing", path);
    public static string Compression_ErrorInvalidFormat(string value) => F("Compression_ErrorInvalidFormat", value);
    public static string Compression_ErrorInvalidLevel(string value) => F("Compression_ErrorInvalidLevel", value);
    public static string Compression_ErrorInvalidEncoding(string value) => F("Compression_ErrorInvalidEncoding", value);
    public static string Compression_ErrorOptionValue(string option) => F("Compression_ErrorOptionValue", option);
    public static string Compression_GeneratePassword => T("Compression_GeneratePassword");
    public static string Compression_PasswordLength => T("Compression_PasswordLength");
    public static string Compression_PasswordLower => T("Compression_PasswordLower");
    public static string Compression_PasswordUpper => T("Compression_PasswordUpper");
    public static string Compression_PasswordDigits => T("Compression_PasswordDigits");
    public static string Compression_PasswordSymbols => T("Compression_PasswordSymbols");
    public static string Compression_PasswordGeneratedNote => T("Compression_PasswordGeneratedNote");
    public static string Compression_PasswordShow => T("Compression_PasswordShow");
    public static string Compression_PasswordHide => T("Compression_PasswordHide");
    public static string Compression_Copy => T("Compression_Copy");
    public static string Compression_Copied => T("Compression_Copied");
    public static string Compression_MacOsCompatible => T("Compression_MacOsCompatible");
    public static string Compression_MacOsCompatibleNote => T("Compression_MacOsCompatibleNote");
    public static string Compression_PasswordGenerateFailed => T("Compression_PasswordGenerateFailed");

    public static string Progress_Extracting => T("Progress_Extracting");
    public static string Progress_Destination(string path) => F("Progress_Destination", path);
    public static string Progress_File(string file) => F("Progress_File", file);
    public static string Progress_CompletedWarnings(int warnings, int skipped) => F("Progress_CompletedWarnings", warnings, skipped);
    public static string Progress_Completed(int files, int skipped) => F("Progress_Completed", files, skipped);
    public static string Progress_Cancelled => T("Progress_Cancelled");
    public static string Progress_ErrorStatus(string message) => F("Progress_ErrorStatus", message);
    public static string Progress_ErrorTitle => T("Progress_ErrorTitle");
    public static string Progress_CancelledTitle => T("Progress_CancelledTitle");
    public static string Progress_FailedMessage => T("Progress_FailedMessage");
    public static string Progress_Cancelling => T("Progress_Cancelling");
    public static string Progress_AutoClose(int seconds) => F("Progress_AutoClose", seconds);
    public static string Progress_UnexpectedError(string message) => F("Progress_UnexpectedError", message);
    public static string Progress_CancelledMessage => T("Progress_CancelledMessage");
    public static string Progress_Compressing => T("Progress_Compressing");
    public static string Progress_Output(string path) => F("Progress_Output", path);
    public static string Progress_CompressionCompleted(int files, string source, string output) => F("Progress_CompressionCompleted", files, source, output);
    public static string Progress_PasswordIgnored => T("Progress_PasswordIgnored");
    public static string Progress_CompressionCancelled => T("Progress_CompressionCancelled");
    public static string Progress_CompressionCancelledTitle => T("Progress_CompressionCancelledTitle");
    public static string Progress_CompressionCancelledMessage => T("Progress_CompressionCancelledMessage");
    public static string Progress_CompressionUnexpectedError(string message) => F("Progress_CompressionUnexpectedError", message);
    public static string Progress_CloseConfirmTitle => T("Progress_CloseConfirmTitle");
    public static string Progress_CloseConfirmMessage => T("Progress_CloseConfirmMessage");
    public static string Progress_CloseConfirmYes => T("Progress_CloseConfirmYes");
    public static string Progress_CloseConfirmNo => T("Progress_CloseConfirmNo");

    public static string Settings_Title => T("Settings_Title");
    public static string Settings_WindowTitle(string product) => F("Settings_WindowTitle", product);
    public static string Settings_Subtitle => T("Settings_Subtitle");
    public static string Settings_Destination => T("Settings_Destination");
    public static string Settings_SameAsArchive => T("Settings_SameAsArchive");
    public static string Settings_DestinationNote => T("Settings_DestinationNote");
    public static string Settings_FolderCollision => T("Settings_FolderCollision");
    public static string Settings_AskEveryTime => T("Settings_AskEveryTime");
    public static string Settings_MergeExisting => T("Settings_MergeExisting");
    public static string Settings_AutoRenameFolder => T("Settings_AutoRenameFolder");
    public static string Settings_FolderCollisionNote => T("Settings_FolderCollisionNote");
    public static string Settings_Overwrite => T("Settings_Overwrite");
    public static string Settings_AlwaysOverwrite => T("Settings_AlwaysOverwrite");
    public static string Settings_AlwaysSkip => T("Settings_AlwaysSkip");
    public static string Settings_RenameSave => T("Settings_RenameSave");
    public static string Settings_OverwriteNote => T("Settings_OverwriteNote");
    public static string Settings_Behavior => T("Settings_Behavior");
    public static string Settings_OpenFolderAfter => T("Settings_OpenFolderAfter");
    public static string Settings_CloseAfter => T("Settings_CloseAfter");
    public static string Settings_CloseAfterNote => T("Settings_CloseAfterNote");
    public static string Settings_Language => T("Settings_Language");
    public static string Settings_LanguageSystem => T("Settings_LanguageSystem");
    public static string Settings_LanguageJapanese => T("Settings_LanguageJapanese");
    public static string Settings_LanguageEnglish => T("Settings_LanguageEnglish");
    public static string Settings_LanguageNote => T("Settings_LanguageNote");
    public static string Settings_Parallelism => T("Settings_Parallelism");
    public static string Settings_ParallelismAuto => T("Settings_ParallelismAuto");
    public static string Settings_ParallelismSequential => T("Settings_ParallelismSequential");
    public static string Settings_ParallelismThreads(int threads) => F("Settings_ParallelismThreads", threads);
    public static string Settings_ParallelismNote => T("Settings_ParallelismNote");
    public static string Settings_LegacyMenu => T("Settings_LegacyMenu");
    public static string Settings_LegacyNote => T("Settings_LegacyNote");
    public static string Settings_LegacyToggle => T("Settings_LegacyToggle");
    public static string Settings_Feedback => T("Settings_Feedback");
    public static string Settings_Support => T("Settings_Support");
    public static string Settings_SupportNote => T("Settings_SupportNote");
    public static string Settings_ContextMenuTitle => T("Settings_ContextMenuTitle");
    public static string Settings_RegisterFailedTitle => T("Settings_RegisterFailedTitle");
    public static string Settings_ContextMenuRegistering => T("Settings_ContextMenuRegistering");
    public static string Settings_Save => T("Settings_Save");
    public static string Settings_Updates => T("Settings_Updates");
    public static string Settings_CheckUpdatesStartup => T("Settings_CheckUpdatesStartup");
    public static string Settings_CheckUpdatesNote => T("Settings_CheckUpdatesNote");
    public static string Settings_CheckUpdatesPackaged => T("Settings_CheckUpdatesPackaged");
    public static string Settings_FixedFolder => T("Settings_FixedFolder");
    public static string Settings_FolderPath => T("Settings_FolderPath");
    public static string Settings_ChooseFolder => T("Settings_ChooseFolder");
    public static string Settings_CompressionDestination => T("Settings_CompressionDestination");
    public static string Settings_SkipNewer => T("Settings_SkipNewer");
    public static string Settings_SkipNewerNote => T("Settings_SkipNewerNote");
    public static string Settings_Associations => T("Settings_Associations");
    public static string Settings_AssociationsNote => T("Settings_AssociationsNote");
    public static string Settings_AssociationsPackaged => T("Settings_AssociationsPackaged");
    public static string Settings_OpenDefaultApps => T("Settings_OpenDefaultApps");
    public static string Settings_DefaultAppsNote => T("Settings_DefaultAppsNote");
    public static string Settings_AssociationEnabled(string extension) => F("Settings_AssociationEnabled", extension);
    public static string Settings_AssociationDisabled(string extension) => F("Settings_AssociationDisabled", extension);
    public static string Settings_AssociationFailed(string extension) => F("Settings_AssociationFailed", extension);
    public static string Settings_AssociationVerifyFailed(string extension) => F("Settings_AssociationVerifyFailed", extension);
    public static string Settings_AssociationFailedDetail(string extension, string message) => F("Settings_AssociationFailedDetail", extension, message);
    public static string Settings_Notifications => T("Settings_Notifications");
    public static string Settings_NotifyOnCompletion => T("Settings_NotifyOnCompletion");
    public static string Settings_NotifyNote => T("Settings_NotifyNote");
    public static string Settings_Background => T("Settings_Background");
    public static string Settings_BackgroundNote => T("Settings_BackgroundNote");
    public static string Settings_Sound => T("Settings_Sound");
    public static string Settings_SoundNone => T("Settings_SoundNone");
    public static string Settings_SoundSystem => T("Settings_SoundSystem");
    public static string Settings_SoundFile => T("Settings_SoundFile");
    public static string Settings_SoundFileLabel => T("Settings_SoundFileLabel");
    public static string Settings_TestSound => T("Settings_TestSound");
    public static string Settings_SoundNote => T("Settings_SoundNote");
    public static string Settings_MacOs => T("Settings_MacOs");
    public static string Settings_ExcludeMacOs => T("Settings_ExcludeMacOs");
    public static string Settings_ExcludeMacOsNote => T("Settings_ExcludeMacOsNote");
    public static string Settings_NormalizeNfc => T("Settings_NormalizeNfc");
    public static string Settings_NormalizeNfcNote => T("Settings_NormalizeNfcNote");

    public static string About_Title => T("About_Title");
    public static string About_WindowTitle(string product) => F("About_WindowTitle", product);
    public static string About_Version(string version) => F("About_Version", version);
    public static string About_Subtitle => T("About_Subtitle");
    public static string About_Tagline => T("About_Tagline");
    public static string About_SupportedFormats => T("About_SupportedFormats");
    public static string About_SupportedFormatsNote => T("About_SupportedFormatsNote");
    public static string About_License => T("About_License");
    public static string About_LicenseFull => T("About_LicenseFull");
    public static string About_Copyright => T("About_Copyright");
    public static string About_Libraries => T("About_Libraries");
    public static string About_SharpCompress => T("About_SharpCompress");
    public static string About_SharpZipLib => T("About_SharpZipLib");
    public static string About_WindowsAppSdk => T("About_WindowsAppSdk");
    public static string About_DotNet => T("About_DotNet");
    public static string About_Privacy => T("About_Privacy");
    public static string About_PrivacyText => T("About_PrivacyText");

    public static string Overwrite_Title => T("Overwrite_Title");
    public static string Overwrite_Exists(string path) => F("Overwrite_Exists", path);
    public static string Overwrite_Item => T("Overwrite_Item");
    public static string Overwrite_ExistingFile => T("Overwrite_ExistingFile");
    public static string Overwrite_NewFile => T("Overwrite_NewFile");
    public static string Overwrite_Size => T("Overwrite_Size");
    public static string Overwrite_Modified => T("Overwrite_Modified");
    public static string Overwrite_Destination => T("Overwrite_Destination");
    public static string Overwrite_Action => T("Overwrite_Action");
    public static string Overwrite_Skip => T("Overwrite_Skip");
    public static string Overwrite_Rename => T("Overwrite_Rename");
    public static string Overwrite_All => T("Overwrite_All");
    public static string Overwrite_SkipAll => T("Overwrite_SkipAll");

    public static string FolderCollision_Title => T("FolderCollision_Title");
    public static string FolderCollision_Exists(string name) => F("FolderCollision_Exists", name);
    public static string FolderCollision_FileConflict => T("FolderCollision_FileConflict");
    public static string FolderCollision_SaveDefault => T("FolderCollision_SaveDefault");
    public static string FolderCollision_Merge => T("FolderCollision_Merge");
    public static string FolderCollision_New => T("FolderCollision_New");
    public static string FolderCollision_Cancel => T("FolderCollision_Cancel");

    public static string Password_Title => T("Password_Title");
    public static string Password_Message => T("Password_Message");
    public static string Password_ErrorWithAttempt(string message, int attempt) => F("Password_ErrorWithAttempt", message, attempt);
    public static string Password_Attempt(int attempt) => F("Password_Attempt", attempt);
    public static string Password_Placeholder => T("Password_Placeholder");
    public static string Password_Ok => T("Password_Ok");
    public static string Password_Cancel => T("Password_Cancel");

    public static string Donation_Title => T("Donation_Title");
    public static string Donation_Message => T("Donation_Message");
    public static string Donation_Note => T("Donation_Note");
    public static string Donation_GitHub => T("Donation_GitHub");
    public static string Donation_Stripe => T("Donation_Stripe");
    public static string Donation_Cancel => T("Donation_Cancel");

    public static string Legacy_PackagedUnsupported => T("Legacy_PackagedUnsupported");
    public static string Legacy_DllMissing(string path) => F("Legacy_DllMissing", path);
    public static string Legacy_Registered => T("Legacy_Registered");
    public static string Legacy_NotRegistered => T("Legacy_NotRegistered");
    public static string Legacy_PackagedCannotRegister => T("Legacy_PackagedCannotRegister");
    public static string Legacy_ClsidCreateFailed => T("Legacy_ClsidCreateFailed");
    public static string Legacy_InprocCreateFailed => T("Legacy_InprocCreateFailed");
    public static string Legacy_HandlerCreateFailed(string extension) => F("Legacy_HandlerCreateFailed", extension);
    public static string Legacy_RegisteredCount(int count) => F("Legacy_RegisteredCount", count);
    public static string Legacy_RegisterFailed(string message) => F("Legacy_RegisterFailed", message);
    public static string Legacy_PackagedNotRegistered => T("Legacy_PackagedNotRegistered");
    public static string Legacy_Unregistered => T("Legacy_Unregistered");
    public static string Legacy_UnregisterFailed(string message) => F("Legacy_UnregisterFailed", message);
    public static string Legacy_VerificationFailed => T("Legacy_VerificationFailed");
    public static string Legacy_UnregisterIncomplete => T("Legacy_UnregisterIncomplete");

    public static string Issue_Generic => T("Issue_Generic");
}
