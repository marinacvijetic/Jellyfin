using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using Jellyfin.Extensions;
using Jellyfin.Extensions.Json;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Globalization;
using Microsoft.Extensions.Logging;

namespace Emby.Server.Implementations.Localization
{
    /// <summary>
    /// Class LocalizationManager.
    /// </summary>
    public class LocalizationManager : ILocalizationManager
    {
        private const string DefaultCulture = "en-US";
        private const string RatingsPath = "Emby.Server.Implementations.Localization.Ratings.";
        private const string CulturesPath = "Emby.Server.Implementations.Localization.iso6392.txt";
        private const string CountriesPath = "Emby.Server.Implementations.Localization.countries.json";
        private static readonly Assembly _assembly = typeof(LocalizationManager).Assembly;
        private static readonly string[] _unratedValues = { "n/a", "unrated", "not rated" };

        private readonly IServerConfigurationManager _configurationManager;
        private readonly ILogger<LocalizationManager> _logger;

        private readonly Dictionary<string, Dictionary<string, ParentalRating>> _allParentalRatings =
            new Dictionary<string, Dictionary<string, ParentalRating>>(StringComparer.OrdinalIgnoreCase);

        private readonly ConcurrentDictionary<string, Dictionary<string, string>> _dictionaries =
            new ConcurrentDictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);

        private readonly JsonSerializerOptions _jsonOptions = JsonDefaults.Options;

        private List<CultureDto> _cultures = new List<CultureDto>();

        /// <summary>
        /// Initializes a new instance of the <see cref="LocalizationManager" /> class.
        /// </summary>
        /// <param name="configurationManager">The configuration manager.</param>
        /// <param name="logger">The logger.</param>
        public LocalizationManager(
            IServerConfigurationManager configurationManager,
            ILogger<LocalizationManager> logger)
        {
            _configurationManager = configurationManager;
            _logger = logger;
        }

        /// <summary>
        /// Loads all resources into memory.
        /// </summary>
        /// <returns><see cref="Task" />.</returns>
        public async Task LoadAll()
        {
            // Extract from the assembly
            foreach (var resource in _assembly.GetManifestResourceNames())
            {
                if (!resource.StartsWith(RatingsPath, StringComparison.Ordinal))
                {
                    continue;
                }

                string countryCode = resource.Substring(RatingsPath.Length, 2);
                var dict = new Dictionary<string, ParentalRating>(StringComparer.OrdinalIgnoreCase);

                await using var stream = _assembly.GetManifestResourceStream(resource);
                using var reader = new StreamReader(stream!); // shouldn't be null here, we just got the resource path from Assembly.GetManifestResourceNames()
                await foreach (var line in reader.ReadAllLinesAsync().ConfigureAwait(false))
                {
                    if (string.IsNullOrWhiteSpace(line))
                    {
                        continue;
                    }

                    string[] parts = line.Split(',');
                    if (parts.Length == 2
                        && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
                    {
                        var name = parts[0];
                        dict.Add(name, new ParentalRating(name, value));
                    }
#if DEBUG
                    else
                    {
                        _logger.LogWarning("Malformed line in ratings file for country {CountryCode}", countryCode);
                    }
#endif
                }

                _allParentalRatings[countryCode] = dict;
            }

            await LoadCultures().ConfigureAwait(false);
        }

        /// <summary>
        /// Gets the cultures.
        /// </summary>
        /// <returns><see cref="IEnumerable{CultureDto}" />.</returns>
        public IEnumerable<CultureDto> GetCultures()
            => _cultures;

        private async Task LoadCultures()
        {
            List<CultureDto> list = new List<CultureDto>();

            await using var stream = _assembly.GetManifestResourceStream(CulturesPath)
                ?? throw new InvalidOperationException($"Invalid resource path: '{CulturesPath}'");
            using var reader = new StreamReader(stream);
            await foreach (var line in reader.ReadAllLinesAsync().ConfigureAwait(false))
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                var parts = line.Split('|');

                if (parts.Length == 5)
                {
                    string name = parts[3];
                    if (string.IsNullOrWhiteSpace(name))
                    {
                        continue;
                    }

                    string twoCharName = parts[2];
                    if (string.IsNullOrWhiteSpace(twoCharName))
                    {
                        continue;
                    }

                    string[] threeletterNames;
                    if (string.IsNullOrWhiteSpace(parts[1]))
                    {
                        threeletterNames = new[] { parts[0] };
                    }
                    else
                    {
                        threeletterNames = new[] { parts[0], parts[1] };
                    }

                    list.Add(new CultureDto(name, name, twoCharName, threeletterNames));
                }
            }

            _cultures = list;
        }

        /// <inheritdoc />
        public CultureDto? FindLanguageInfo(string language)
        {
            // TODO language should ideally be a ReadOnlySpan but moq cannot mock ref structs
            for (var i = 0; i < _cultures.Count; i++)
            {
                var culture = _cultures[i];
                if (language.Equals(culture.DisplayName, StringComparison.OrdinalIgnoreCase)
                    || language.Equals(culture.Name, StringComparison.OrdinalIgnoreCase)
                    || culture.ThreeLetterISOLanguageNames.Contains(language, StringComparison.OrdinalIgnoreCase)
                    || language.Equals(culture.TwoLetterISOLanguageName, StringComparison.OrdinalIgnoreCase))
                {
                    return culture;
                }
            }

            return default;
        }

        /// <inheritdoc />
        public IEnumerable<CountryInfo> GetCountries()
        {
            using StreamReader reader = new StreamReader(
                _assembly.GetManifestResourceStream(CountriesPath) ?? throw new InvalidOperationException($"Invalid resource path: '{CountriesPath}'"));
            return JsonSerializer.Deserialize<IEnumerable<CountryInfo>>(reader.ReadToEnd(), _jsonOptions)
                ?? throw new InvalidOperationException($"Resource contains invalid data: '{CountriesPath}'");
        }

        /// <inheritdoc />
        public IEnumerable<ParentalRating> GetParentalRatings()
            => GetParentalRatingsDictionary().Values;

        /// <summary>
        /// Gets the parental ratings dictionary.
        /// </summary>
        /// <returns><see cref="Dictionary{String, ParentalRating}" />.</returns>
        private Dictionary<string, ParentalRating> GetParentalRatingsDictionary()
        {
            var countryCode = _configurationManager.Configuration.MetadataCountryCode;

            if (string.IsNullOrEmpty(countryCode))
            {
                countryCode = "us";
            }

            return GetRatings(countryCode)
                ?? GetRatings("us")
                ?? throw new InvalidOperationException($"Invalid resource path: '{CountriesPath}'");
        }

        /// <summary>
        /// Gets the ratings.
        /// </summary>
        /// <param name="countryCode">The country code.</param>
        /// <returns>The ratings.</returns>
        private Dictionary<string, ParentalRating>? GetRatings(string countryCode)
        {
            _allParentalRatings.TryGetValue(countryCode, out var value);

            return value;
        }

        /// <inheritdoc />
        public int? GetRatingLevel(string rating)
        {
            if (string.IsNullOrEmpty(rating))
            {
                throw new ArgumentNullException(nameof(rating));
            }

            if (_unratedValues.Contains(rating.AsSpan(), StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            // Fairly common for some users to have "Rated R" in their rating field
            rating = rating.Replace("Rated ", string.Empty, StringComparison.OrdinalIgnoreCase);

            var ratingsDictionary = GetParentalRatingsDictionary();

            if (ratingsDictionary.TryGetValue(rating, out ParentalRating? value))
            {
                return value.Value;
            }

            // If we don't find anything check all ratings systems
            foreach (var dictionary in _allParentalRatings.Values)
            {
                if (dictionary.TryGetValue(rating, out value))
                {
                    return value.Value;
                }
            }

            // Try splitting by : to handle "Germany: FSK 18"
            var index = rating.IndexOf(':', StringComparison.Ordinal);
            if (index != -1)
            {
                var trimmedRating = rating.AsSpan(index).TrimStart(':').Trim();

                if (!trimmedRating.IsEmpty)
                {
                    return GetRatingLevel(trimmedRating.ToString());
                }
            }

            // TODO: Further improve by normalizing out all spaces and dashes
            return null;
        }

        /// <inheritdoc />
        public string GetLocalizedString(string phrase)
        {
            return GetLocalizedString(phrase, _configurationManager.Configuration.UICulture);
        }

        /// <inheritdoc />
        public string GetLocalizedString(string phrase, string culture)
        {
            if (string.IsNullOrEmpty(culture))
            {
                culture = _configurationManager.Configuration.UICulture;
            }

            if (string.IsNullOrEmpty(culture))
            {
                culture = DefaultCulture;
            }

            var dictionary = GetLocalizationDictionary(culture);

            if (dictionary.TryGetValue(phrase, out var value))
            {
                return value;
            }

            return phrase;
        }

        private Dictionary<string, string> GetLocalizationDictionary(string culture)
        {
            if (string.IsNullOrEmpty(culture))
            {
                throw new ArgumentNullException(nameof(culture));
            }

            const string Prefix = "Core";

            return _dictionaries.GetOrAdd(
                culture,
                static (key, localizationManager) => localizationManager.GetDictionary(Prefix, key, DefaultCulture + ".json").GetAwaiter().GetResult(),
                this);
        }

        private async Task<Dictionary<string, string>> GetDictionary(string prefix, string culture, string baseFilename)
        {
            if (string.IsNullOrEmpty(culture))
            {
                throw new ArgumentNullException(nameof(culture));
            }

            var dictionary = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            var namespaceName = GetType().Namespace + "." + prefix;

            await CopyInto(dictionary, namespaceName + "." + baseFilename).ConfigureAwait(false);
            await CopyInto(dictionary, namespaceName + "." + GetResourceFilename(culture)).ConfigureAwait(false);

            return dictionary;
        }

        private async Task CopyInto(IDictionary<string, string> dictionary, string resourcePath)
        {
            await using var stream = _assembly.GetManifestResourceStream(resourcePath);
            // If a Culture doesn't have a translation the stream will be null and it defaults to en-us further up the chain
            if (stream == null)
            {
                _logger.LogError("Missing translation/culture resource: {ResourcePath}", resourcePath);
                return;
            }

            var dict = await JsonSerializer.DeserializeAsync<Dictionary<string, string>>(stream, _jsonOptions).ConfigureAwait(false);
            if (dict == null)
            {
                throw new InvalidOperationException($"Resource contains invalid data: '{stream}'");
            }

            foreach (var key in dict.Keys)
            {
                dictionary[key] = dict[key];
            }
        }

        private static string GetResourceFilename(string culture)
        {
            var parts = culture.Split('-');

            if (parts.Length == 2)
            {
                culture = parts[0].ToLowerInvariant() + "-" + parts[1].ToUpperInvariant();
            }
            else
            {
                culture = culture.ToLowerInvariant();
            }

            return culture + ".json";
        }

        /// <inheritdoc />
        public IEnumerable<LocalizationOption> GetLocalizationOptions()
        {
            yield return new LocalizationOption("Afrikaans", "af");
            yield return new LocalizationOption("??????????????", "ar");
            yield return new LocalizationOption("????????????????????", "be");
            yield return new LocalizationOption("??????????????????", "bg-BG");
            yield return new LocalizationOption("??????????????? (????????????????????????)", "bn");
            yield return new LocalizationOption("Catal??", "ca");
            yield return new LocalizationOption("??e??tina", "cs");
            yield return new LocalizationOption("Cymraeg", "cy");
            yield return new LocalizationOption("Dansk", "da");
            yield return new LocalizationOption("Deutsch", "de");
            yield return new LocalizationOption("English (United Kingdom)", "en-GB");
            yield return new LocalizationOption("English", "en-US");
            yield return new LocalizationOption("????????????????", "el");
            yield return new LocalizationOption("Esperanto", "eo");
            yield return new LocalizationOption("Espa??ol", "es");
            yield return new LocalizationOption("Espa??ol americano", "es_419");
            yield return new LocalizationOption("Espa??ol (Argentina)", "es-AR");
            yield return new LocalizationOption("Espa??ol (Dominicana)", "es_DO");
            yield return new LocalizationOption("Espa??ol (M??xico)", "es-MX");
            yield return new LocalizationOption("Eesti", "et");
            yield return new LocalizationOption("Basque", "eu");
            yield return new LocalizationOption("??????????", "fa");
            yield return new LocalizationOption("Suomi", "fi");
            yield return new LocalizationOption("Filipino", "fil");
            yield return new LocalizationOption("Fran??ais", "fr");
            yield return new LocalizationOption("Fran??ais (Canada)", "fr-CA");
            yield return new LocalizationOption("Galego", "gl");
            yield return new LocalizationOption("Schwiizerd??tsch", "gsw");
            yield return new LocalizationOption("????????????????", "he");
            yield return new LocalizationOption("??????????????????", "hi");
            yield return new LocalizationOption("Hrvatski", "hr");
            yield return new LocalizationOption("Magyar", "hu");
            yield return new LocalizationOption("Bahasa Indonesia", "id");
            yield return new LocalizationOption("??slenska", "is");
            yield return new LocalizationOption("Italiano", "it");
            yield return new LocalizationOption("?????????", "ja");
            yield return new LocalizationOption("Qazaq??a", "kk");
            yield return new LocalizationOption("?????????", "ko");
            yield return new LocalizationOption("Lietuvi??", "lt");
            yield return new LocalizationOption("Latvie??u", "lv");
            yield return new LocalizationOption("????????????????????", "mk");
            yield return new LocalizationOption("??????????????????", "ml");
            yield return new LocalizationOption("???????????????", "mr");
            yield return new LocalizationOption("Bahasa Melayu", "ms");
            yield return new LocalizationOption("Norsk bokm??l", "nb");
            yield return new LocalizationOption("??????????????????", "ne");
            yield return new LocalizationOption("Nederlands", "nl");
            yield return new LocalizationOption("Norsk nynorsk", "nn");
            yield return new LocalizationOption("??????????????????", "pa");
            yield return new LocalizationOption("Polski", "pl");
            yield return new LocalizationOption("Pirate", "pr");
            yield return new LocalizationOption("Portugu??s", "pt");
            yield return new LocalizationOption("Portugu??s (Brasil)", "pt-BR");
            yield return new LocalizationOption("Portugu??s (Portugal)", "pt-PT");
            yield return new LocalizationOption("Rom??ne??te", "ro");
            yield return new LocalizationOption("??????????????", "ru");
            yield return new LocalizationOption("Sloven??ina", "sk");
            yield return new LocalizationOption("Sloven????ina", "sl-SI");
            yield return new LocalizationOption("Shqip", "sq");
            yield return new LocalizationOption("????????????", "sr");
            yield return new LocalizationOption("Svenska", "sv");
            yield return new LocalizationOption("???????????????", "ta");
            yield return new LocalizationOption("??????????????????", "te");
            yield return new LocalizationOption("?????????????????????", "th");
            yield return new LocalizationOption("T??rk??e", "tr");
            yield return new LocalizationOption("????????????????????", "uk");
            yield return new LocalizationOption("????????????", "ur_PK");
            yield return new LocalizationOption("Ti???ng Vi???t", "vi");
            yield return new LocalizationOption("?????? (?????????)", "zh-CN");
            yield return new LocalizationOption("?????? (?????????)", "zh-TW");
            yield return new LocalizationOption("????????? (??????)", "zh-HK");
        }
    }
}
