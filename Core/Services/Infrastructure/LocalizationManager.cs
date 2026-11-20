using System.Windows;

namespace CortexFX.Core.Services.Infrastructure;

/// <summary>
/// Live runtime UI localization manager (English / Arabic).
/// Swaps Application ResourceDictionaries instantly without restart while keeping
/// file formats, extensions, technical specs, and brand identifiers in English.
/// </summary>
public static class Loc
{
    private const string MarkerKey = "__LocDictionary";
    private static ResourceDictionary? _activeDict;
    private static ResourceDictionary? _englishFallback;

    public static string CurrentLanguage { get; private set; } = "en";
    public static bool IsArabic => string.Equals(CurrentLanguage, "ar", StringComparison.OrdinalIgnoreCase);
    public static FlowDirection TextFlowDirection => IsArabic ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;

    public static event Action? LanguageChanged;

    public static void Initialize()
    {
        string saved = UserPreferences.Current.Language;
        SetLanguage(saved, save: false);
    }

    public static void SetLanguage(string languageCode, bool save = true)
    {
        string normalized = string.Equals(languageCode, "ar", StringComparison.OrdinalIgnoreCase) ? "ar" : "en";

        if (save)
        {
            UserPreferences.Current.Language = normalized;
            UserPreferences.Current.Save();
        }

        ResourceDictionary? dict = LoadDictionary(normalized);
        _englishFallback ??= normalized == "en" ? dict : LoadDictionary("en");

        CurrentLanguage = normalized;
        if (dict != null)
        {
            _activeDict = dict;
            ApplyToApplication(dict);
        }

        LanguageChanged?.Invoke();
    }

    public static string T(string key, string? fallback = null)
    {
        if (string.IsNullOrEmpty(key))
            return fallback ?? string.Empty;

        if (_activeDict != null && _activeDict.Contains(key) && _activeDict[key] is string s)
            return s;

        if (Application.Current?.Resources != null &&
            Application.Current.Resources.Contains(key) &&
            Application.Current.Resources[key] is string appVal)
            return appVal;

        if (_englishFallback != null && _englishFallback.Contains(key) && _englishFallback[key] is string enVal)
            return enVal;

        return fallback ?? key;
    }

    public static string F(string key, params object[] args)
    {
        string format = T(key);
        try
        {
            return string.Format(format, args);
        }
        catch
        {
            return format;
        }
    }

    /// <summary>
    /// Automatically translates standard English dialog button labels when Arabic UI is active.
    /// </summary>
    public static string TranslateDialogButton(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return T("Loc_Dlg_OK", "OK");

        if (!IsArabic)
            return text;

        return text.Trim() switch
        {
            "OK" => T("Loc_Dlg_OK", "حسناً"),
            "Done" => T("Loc_Dlg_Done", "تم"),
            "Yes" => T("Loc_Dlg_Yes", "نعم"),
            "No" => T("Loc_Dlg_No", "لا"),
            "Save" => T("Loc_Dlg_Save", "حفظ"),
            "Cancel" => T("Loc_Dlg_Cancel", "إلغاء"),
            "Close" => T("Loc_Dlg_Close", "إغلاق"),
            "Confirm" => T("Loc_Dlg_Confirm", "تأكيد"),
            "Copy details" => T("Loc_Dlg_CopyDetails", "نسخ التفاصيل"),
            "Copied" => T("Loc_Dlg_Copied", "تم النسخ"),
            "Later" => T("Loc_Upd_LaterBtn", "لاحقاً"),
            "Install Update" => T("Loc_Upd_InstallBtn", "تثبيت التحديث"),
            "Save Copy" => T("Loc_VC_SaveCopyBtn", "حفظ النسخة"),
            "Rename" => T("Loc_VC_RenameBtn", "إعادة تسمية"),
            "Open Folder" => T("Loc_Overlay_OpenFolder", "فتح المجلد"),
            "Open Logs Folder" => T("Loc_Overlay_OpenLogs", "فتح مجلد السجلات"),
            "Open file location" => T("Loc_Dlg_OpenLocation", "فتح موقع الملف"),
            _ => text
        };
    }

    private static ResourceDictionary? LoadDictionary(string code)
    {
        try
        {
            var uri = new Uri($"pack://application:,,,/CortexFX;component/Resources/Localization/Strings.{code}.xaml", UriKind.Absolute);
            return new ResourceDictionary { Source = uri };
        }
        catch
        {
            try
            {
                var relUri = new Uri($"/CortexFX;component/Resources/Localization/Strings.{code}.xaml", UriKind.Relative);
                return Application.LoadComponent(relUri) as ResourceDictionary;
            }
            catch
            {
                return null;
            }
        }
    }

    private static void ApplyToApplication(ResourceDictionary newDict)
    {
        if (Application.Current?.Resources == null)
            return;

        var merged = Application.Current.Resources.MergedDictionaries;
        for (int i = merged.Count - 1; i >= 0; i--)
        {
            if (merged[i].Contains(MarkerKey))
            {
                merged.RemoveAt(i);
            }
        }
        merged.Add(newDict);
    }
}
