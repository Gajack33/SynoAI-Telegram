using Newtonsoft.Json;
using SynoAI.Models;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace SynoAI.Notifiers.Telegram
{
    internal static class TelegramTranslationCatalog
    {
        private const string DefaultLanguage = "en";
        private const string TranslationFileName = "telegram-translations.json";

        private static readonly Lazy<IReadOnlyDictionary<string, TelegramTranslation>> Translations = new(LoadTranslations);

        public static TelegramTranslation Get(string language)
        {
            IReadOnlyDictionary<string, TelegramTranslation> translations = Translations.Value;
            string requestedLanguage = string.IsNullOrWhiteSpace(language) ? DefaultLanguage : language.Trim();

            if (translations.TryGetValue(requestedLanguage, out TelegramTranslation translation))
            {
                return translation;
            }

            string neutralLanguage = requestedLanguage.Split('-', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(neutralLanguage) &&
                translations.TryGetValue(neutralLanguage, out translation))
            {
                return translation;
            }

            return translations[DefaultLanguage];
        }

        private static IReadOnlyDictionary<string, TelegramTranslation> LoadTranslations()
        {
            string path = Path.Combine(AppContext.BaseDirectory, "Notifiers", "Telegram", TranslationFileName);
            if (!File.Exists(path))
            {
                path = Path.Combine(AppContext.BaseDirectory, TranslationFileName);
            }

            Dictionary<string, TelegramTranslation> translations = File.Exists(path)
                ? JsonConvert.DeserializeObject<Dictionary<string, TelegramTranslation>>(File.ReadAllText(path))
                : null;

            translations = translations == null
                ? CreateFallbackTranslations()
                : new Dictionary<string, TelegramTranslation>(translations, StringComparer.OrdinalIgnoreCase);
            if (!translations.ContainsKey(DefaultLanguage))
            {
                translations[DefaultLanguage] = CreateEnglishFallback();
            }

            return translations;
        }

        private static Dictionary<string, TelegramTranslation> CreateFallbackTranslations()
        {
            return new Dictionary<string, TelegramTranslation>(StringComparer.OrdinalIgnoreCase)
            {
                [DefaultLanguage] = CreateEnglishFallback()
            };
        }

        private static TelegramTranslation CreateEnglishFallback()
        {
            return new TelegramTranslation
            {
                Culture = "en-US",
                PhotoCaptionTitle = "Camera alert - {cameraName}",
                TimeLabel = "Time",
                DetectionLabel = "Detection",
                VideoCaption = "Video clip - {cameraName}",
                DefaultObject = "Object detected",
                DefaultObjectPlural = "Objects",
                Labels = new Dictionary<string, TelegramLabelTranslation>(StringComparer.OrdinalIgnoreCase)
                {
                    ["person"] = new() { Singular = "Person", Plural = "People" },
                    ["car"] = new() { Singular = "Car", Plural = "Cars" },
                    ["truck"] = new() { Singular = "Truck", Plural = "Trucks" },
                    ["bus"] = new() { Singular = "Bus", Plural = "Buses" },
                    ["bicycle"] = new() { Singular = "Bicycle", Plural = "Bicycles" },
                    ["motorbike"] = new() { Singular = "Motorbike", Plural = "Motorbikes" },
                    ["motorcycle"] = new() { Singular = "Motorcycle", Plural = "Motorcycles" },
                    ["cat"] = new() { Singular = "Cat", Plural = "Cats" },
                    ["dog"] = new() { Singular = "Dog", Plural = "Dogs" }
                }
            };
        }
    }

    internal sealed class TelegramTranslation
    {
        public string Culture { get; set; } = "en-US";
        public string PhotoCaptionTitle { get; set; } = "Camera alert - {cameraName}";
        public string TimeLabel { get; set; } = "Time";
        public string DetectionLabel { get; set; } = "Detection";
        public string VideoCaption { get; set; } = "Video clip - {cameraName}";
        public string DefaultObject { get; set; } = "Object detected";
        public string DefaultObjectPlural { get; set; } = "Objects";
        public Dictionary<string, TelegramLabelTranslation> Labels { get; set; } = new(StringComparer.OrdinalIgnoreCase);

        public CultureInfo GetCulture()
        {
            try
            {
                return CultureInfo.GetCultureInfo(Culture);
            }
            catch (CultureNotFoundException)
            {
                return CultureInfo.GetCultureInfo("en-US");
            }
        }

        public string Format(string template, Camera camera)
        {
            return (template ?? string.Empty).Replace("{cameraName}", camera.Name, StringComparison.Ordinal);
        }
    }

    internal sealed class TelegramLabelTranslation
    {
        public string Singular { get; set; }
        public string Plural { get; set; }
    }
}
