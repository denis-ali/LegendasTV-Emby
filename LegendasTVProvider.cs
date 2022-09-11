using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using HtmlAgilityPack;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Controller.Security;
using MediaBrowser.Controller.Subtitles;
using MediaBrowser.Model.Globalization;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Providers;
using MediaBrowser.Model.Serialization;

namespace LegendasTV
{
    class LegendasTVProvider : ISubtitleProvider, IDisposable
    {
        public static readonly string URL_BASE = "http://legendas.tv";
        public static readonly string URL_LOGIN = $"{URL_BASE}/login";

        private readonly ILogger _logger;
        private readonly IHttpClient _httpClient;
        private readonly IServerConfigurationManager _config;
        private readonly IEncryptionManager _encryption;
        private readonly ILibraryManager _libraryManager;
        private readonly ILocalizationManager _localizationManager;
        private readonly IJsonSerializer _jsonSerializer;

        private const string PasswordHashPrefix = "h:";

        public string Name => "Legendas TV";

        public IEnumerable<VideoContentType> SupportedMediaTypes => new[] { VideoContentType.Episode, VideoContentType.Movie };

        public LegendasTVProvider(ILogger logger, IHttpClient httpClient, IServerConfigurationManager config, IEncryptionManager encryption,
                                  ILocalizationManager localizationManager, ILibraryManager libraryManager, IJsonSerializer jsonSerializer,
                                  IServerApplicationPaths appPaths, IFileSystem fileSystem, IZipClient zipClient)
        {
            _logger = logger;
            _httpClient = httpClient;
            _config = config;
            _encryption = encryption;
            _libraryManager = libraryManager;
            _localizationManager = localizationManager;
            _jsonSerializer = jsonSerializer;

            _config.NamedConfigurationUpdating += _config_NamedConfigurationUpdating;

            // Load HtmlAgilityPack from embedded resource
            EmbeddedAssembly.Load(GetType().Namespace + ".HtmlAgilityPack.dll", "HtmlAgilityPack.dll");

            AppDomain.CurrentDomain.AssemblyResolve += new ResolveEventHandler((object sender, ResolveEventArgs args) => EmbeddedAssembly.Get(args.Name));
        }

        private LegendasTVOptions GetOptions() => _config.GetLegendasTVConfiguration();

        public async Task<IEnumerable<RemoteSubtitleInfo>> Search(SubtitleSearchRequest request, CancellationToken cancellationToken)
        {
            var lang = _localizationManager.FindLanguageInfo(request.Language.AsSpan());

            if (request.IsForced.HasValue || request.IsPerfectMatch || GetLanguageId(lang) == null)
            {
                return Array.Empty<RemoteSubtitleInfo>();
            }

            if (await Login(cancellationToken))
            {
                BaseItem item = _libraryManager.FindByPath(request.MediaPath, false);

                var legendatvIds = FindIds(request, item, cancellationToken);
                var searchTasks = new List<Task<IEnumerable<RemoteSubtitleInfo>>>();

                Action<string> addSearchTask;

                switch (request.ContentType)
                {
                    // Series Episode
                    case VideoContentType.Episode:
                    {
                        addSearchTask = (id) =>
                        {
                            searchTasks.Add(Search(item.Id, cancellationToken, itemId: id, lang: lang, query: $"S{request.ParentIndexNumber:D02}E{request.IndexNumber:D02}"));
                            searchTasks.Add(Search(item.Id, cancellationToken, itemId: id, lang: lang, query: $"{request.ParentIndexNumber:D02}x{request.IndexNumber:D02}"));
                        };

                        break;
                    }

                    // Movie
                    case VideoContentType.Movie:
                    default:
                    {
                        addSearchTask = (id) =>
                        {
                            searchTasks.Add(Search(item.Id, cancellationToken, lang: lang, itemId: id));
                        };

                        break;
                    }
                }

                foreach (var id in legendatvIds)
                {
                    addSearchTask(id);
                }

                await Task.WhenAll(searchTasks);

                return searchTasks.SelectMany(t => t.Result);
            }
            else
            {
                return Array.Empty<RemoteSubtitleInfo>();
            }
        }

        private IEnumerable<string> FindIds(SubtitleSearchRequest request, BaseItem item, CancellationToken cancellationToken, int depth = 0)
        {
            var query = request.ContentType == VideoContentType.Episode ? ((Episode)item).Series.OriginalTitle : item.OriginalTitle;
            string imdbId = request.ContentType == VideoContentType.Episode ? ((Episode)item).Series.ProviderIds["Imdb"].Substring(2) : item.ProviderIds["Imdb"].Substring(2);

            if (!int.TryParse(imdbId, out var imdbIdInt)) imdbIdInt = -1;

            var requestOptions = new HttpRequestOptions()
            {
                Url = $"{URL_BASE}/legenda/sugestao/{HttpUtility.HtmlEncode(ReplaceSpecialCharacters(query, " "))}",
                CancellationToken = cancellationToken
            };

            var response = String.Empty;

            var t = Task.Run(async delegate
            {
                var pass = 1;

                do
                {
                    using (var stream = _httpClient.Get(requestOptions).Result)
                    {
                        using (var reader = new StreamReader(stream))
                        {
                            response = reader.ReadToEnd();

                            await Task.Delay(1000);
                        }
                    }
                } while ((String.IsNullOrEmpty(response) || response == "[]") && pass++ <= 10);

                _logger.Info(pass <= 10 ? $"Success on attempt: {pass}" : $"Legendas.tv has not returned suggestions after {pass} attempts.");
            });

            t.Wait();

            var suggestions = _jsonSerializer.DeserializeFromString<List<LegendasTVSuggestion>>(response);

            foreach (var suggestion in suggestions)
            {
                var source = suggestion._source;

                if (!int.TryParse(source.id_imdb, out var sourceImdb)) sourceImdb = -2;

                if (((sourceImdb == imdbIdInt) || (source.id_imdb == imdbId)) && (request.ContentType != VideoContentType.Movie || source.tipo == "M"))
                {
                    yield return source.id_filme;
                }
            }
        }

        public async Task<IEnumerable<RemoteSubtitleInfo>> Search(Guid idMedia, CancellationToken cancellationToken, CultureDto lang = null, string query = "-", string page = "-", string itemId = "-")
        {
            if (lang == null)
            {
                _logger.Error("No language defined.");

                return Array.Empty<RemoteSubtitleInfo>();
            }

            var requestOptions = new HttpRequestOptions()
            {
                Url = $"{URL_BASE}/legenda/busca/{HttpUtility.HtmlEncode(query)}/{GetLanguageId(lang)}/-/{page}/{itemId}",
                CancellationToken = cancellationToken,
                Referer = $"{URL_BASE}/busca/{query}"
            };

            requestOptions.RequestHeaders.Add("X-Requested-With", "XMLHttpRequest");

            using (var stream = await _httpClient.Get(requestOptions))
            {
                using (var reader = new StreamReader(stream))
                {
                    var response = reader.ReadToEnd();

                    return ParseHtml(idMedia, response, lang)
                        .OrderBy(sub => LegendasTVIdParts.Parse(sub.Id).sortingOverride)
                        .ThenByDescending(sub => sub.CommunityRating)
                        .ThenByDescending(sub => sub.DownloadCount);
                }
            }
        }

        private IEnumerable<RemoteSubtitleInfo> ParseHtml(Guid idMedia, string html, CultureDto lang)
        {
            var doc = new HtmlDocument();

            doc.LoadHtml(html);

            var subtitleNodes = doc.DocumentNode.SelectNodes("//div[contains(@class, 'list_element')]//article/div") ?? new HtmlNodeCollection(doc.DocumentNode);

            foreach (var subtitleNode in subtitleNodes)
            {
                var link = subtitleNode.SelectSingleNode(".//a");
                var data = subtitleNode.SelectSingleNode(".//p[contains(@class, 'data')]");
                var dataMatch = Regex.Match(data.InnerText.Trim(), @"^\D*?(\d+) +downloads,.*nota +(\d+) *,.*em *(.+)$").Groups;
                var downloadId = Regex.Match(link.Attributes["href"].Value, @"^.*download\/(.*?)\/.*$").Groups[1].Value;
                var name = link.InnerText;

                yield return new RemoteSubtitleInfo()
                {
                    Id = new LegendasTVIdParts()
                    {
                        downloadId = downloadId,
                        name = name,
                        language = lang.TwoLetterISOLanguageName,
                        sortingOverride = subtitleNode.HasClass("destaque") ? -1 : 0,
                        idMedia = idMedia
                    }.fullId,
                    Name = name,
                    DownloadCount = int.Parse(dataMatch[1].Value),
                    CommunityRating = float.Parse(dataMatch[2].Value),
                    DateCreated = DateTimeOffset.ParseExact("15/11/2019 - 12:43", "dd/MM/yyyy - HH:mm", CultureInfo.InvariantCulture),
                    Format = "srt",
                    IsForced = false,
                    IsHashMatch = false,
                    ProviderName = this.Name,
                    Author = data.SelectSingleNode("//a")?.InnerText,
                    Language = lang.ThreeLetterISOLanguageName
                };
            }

        }

        public async Task<bool> Login(CancellationToken cancellationToken)
        {
            var doc = new HtmlDocument();
            var options = GetOptions();
            var username = options.LegendasTVUsername;

            var requestOptions = new HttpRequestOptions()
            {
                Url = $"{URL_BASE}",
                CancellationToken = cancellationToken
            };

            using (var stream = await _httpClient.Get(requestOptions))
            {
                using (var reader = new StreamReader(stream))
                {
                    var html = reader.ReadToEnd();

                    doc.LoadHtml(html);

                    var loginNode = doc.DocumentNode.SelectSingleNode("//div[contains(@class, 'login')]") ?? new HtmlNode(HtmlNodeType.Element, doc, 0);
                    var usernameNode = loginNode.SelectSingleNode(".//a").InnerText.ToUpperInvariant();

                    if (usernameNode == username.ToUpperInvariant())
                    {
                        _logger.Info($"User {username} already logged.");

                        return true;
                    }
                }
            }

            var password = DecryptPassword(options.LegendasTVPasswordHash);

            string result = await SendPost(URL_LOGIN, $"data[User][username]={username}&data[User][password]={password}");

            if (result.Contains("Usuário ou senha inválidos"))
            {
                _logger.Error("Invalid username or password");

                return false;
            }

            return true;
        }

        public async Task<SubtitleResponse> GetSubtitles(string id, CancellationToken cancellationToken)
        {
            return await GetSubtitles(id, cancellationToken, 0);
        }

        public async Task<SubtitleResponse> GetSubtitles(string id, CancellationToken cancellationToken, int depth = 0)
        {
            var idParts = LegendasTVIdParts.Parse(id);

            var requestOptions = new HttpRequestOptions()
            {
                Url = $"{URL_BASE}/downloadarquivo/{idParts.downloadId}",
                CancellationToken = cancellationToken,
            };

            try
            {
                using (var responseStream = await _httpClient.Get(requestOptions))
                {
                    _logger.Info($"Extracting file to memory");

                    using (var zip = new ZipArchive(responseStream, ZipArchiveMode.Read))
                    {
                        var mediaItem = _libraryManager.GetItemById(idParts.idMedia);
                        var bestMatch = FindBestMatch(mediaItem.FileNameWithoutExtension, zip.Entries);

                        _logger.Info($"Best subtitle found: {bestMatch}");

                        var entryStream = new MemoryStream();

                        await zip.Entries.FirstOrDefault(e => e.FullName == bestMatch).Open().CopyToAsync(entryStream);

                        entryStream.Position = 0;

                        return new SubtitleResponse()
                        {
                            Format = Regex.Replace(Path.GetExtension(bestMatch), @"\.+", ""),
                            IsForced = false,
                            Stream = entryStream,
                            Language = idParts.language
                        };
                    }
                }
            }
            catch (System.Exception)
            {
                if (depth < 1 && await Login(cancellationToken))
                {
                    return await GetSubtitles(id, cancellationToken, depth + 1);
                }
                else
                {
                    throw;
                }
            }
        }

        private string FindBestMatch(string name, IReadOnlyCollection<ZipArchiveEntry> subtitleFiles)
        {
            if (!subtitleFiles.Any()) return "";

            var fileNameParts = BreakNameInParts(name);
            var candidates = new List<Tuple<string, int>>();

            foreach (var file in subtitleFiles)
            {
                var subtitleParts = BreakNameInParts(Path.GetFileNameWithoutExtension(file.Name));
                var exceptions = fileNameParts.Except(subtitleParts);

                candidates.Add(new Tuple<string, int>(file.FullName, exceptions.Count()));
            }

            var sorted = candidates.OrderBy(t => t.Item2);

            _logger.Info($"Media file: {name}");

            foreach (var compareResult in sorted)
            {
                _logger.Info($"File: {compareResult.Item1} has {compareResult.Item2} difference(s)");
            }

            return sorted.First().Item1;
        }

        private IEnumerable<string> BreakNameInParts(string name)
        {
            name = name.ToLowerInvariant();
            name = Regex.Replace(name, "[-\\.,\\[\\]_=]", " ");
            name = name.Trim();
            name = Regex.Replace(name, " +", " ");

            return name.Split(' ');
        }

        private string GetLanguageId(CultureDto cultureDto)
        {
            var search = cultureDto?.TwoLetterISOLanguageName ?? "";

            if (search != "pt-br")
            {
                search = search.Split(new[] { '-' }, 2)?[0] ?? search;
            }

            _logger.Info($"Searching language: {search}");

            var langMap = new Dictionary<string, string>()
            {
                {"pt-br", "1"},
                {"pt", "2"},
                {"en", "3"},
                {"fr", "4"},
                {"de", "5"},
                {"ja", "6"},
                {"da", "7"},
                {"nb", "8"},
                {"sv", "9"},
                {"es", "10"},
                {"ar", "11"},
                {"cs", "12"},
                {"zh", "13"},
                {"ko", "14"},
                {"bg", "15"},
                {"it", "16"},
                {"pl", "17"}
            };

            return langMap.TryGetValue(search, out string output) ? output : null;
        }

        private string ReplaceSpecialCharacters(string str, string rplc)
        {
            return Regex.Replace(str, "[^a-zA-Z0-9_.]+", rplc, RegexOptions.Compiled);
        }

        private async Task<string> SendPost(string url, string postData)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(postData);

            var requestOptions = new HttpRequestOptions()
            {
                Url = url,
                RequestContentType = "application/x-www-form-urlencoded",
                RequestContentBytes = bytes
            };

            var response = await _httpClient.Post(requestOptions);

            using (var stream = response.Content)
            {
                using (var reader = new StreamReader(stream))
                {
                    return reader.ReadToEnd();
                }
            }
        }

        void _config_NamedConfigurationUpdating(object sender, ConfigurationUpdateEventArgs e)
        {
            if (!string.Equals(e.Key, "legendastv", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var options = (LegendasTVOptions)e.NewConfiguration;

            if (options != null && !string.IsNullOrWhiteSpace(options.LegendasTVPasswordHash) &&
                !options.LegendasTVPasswordHash.StartsWith(PasswordHashPrefix, StringComparison.OrdinalIgnoreCase))
            {
                options.LegendasTVPasswordHash = EncryptPassword(options.LegendasTVPasswordHash);
            }
        }

        private string EncryptPassword(string password)
        {
            return PasswordHashPrefix + _encryption.EncryptString(password);
        }

        private string DecryptPassword(string password)
        {
            if (password == null ||
                !password.StartsWith(PasswordHashPrefix, StringComparison.OrdinalIgnoreCase))
            {
                return string.Empty;
            }

            return _encryption.DecryptString(password.Substring(2));
        }

        public void Dispose() => GC.SuppressFinalize(this);

        ~LegendasTVProvider() => Dispose();
    }

    class LegendasTVIdParts
    {
        public string downloadId;
        public string name;
        public string language;
        public Guid idMedia;
        /// <summary>Set to higher number if this is to be sorted higher than the other parameters.</summary>
        public int sortingOverride = 0;

        public LegendasTVIdParts() { }

        public override string ToString() => fullId;

        public static LegendasTVIdParts Parse(string id)
        {
            var idParts = id.Split(new[] { ':' }, 5);

            return new LegendasTVIdParts()
            {
                downloadId = idParts[0],
                name = idParts[1],
                language = idParts[2],
                sortingOverride = int.Parse(idParts[3]),
                idMedia = Guid.Parse(idParts[4])
            };
        }

        public string fullId
        {
            get => String.Join(":", downloadId, name, language, sortingOverride, idMedia.ToString());
        }
    }

    class LegendasTVSuggestion
    {
        public class LegendasTVSource
        {
            public string id_filme { get; set; }
            public string id_imdb { get; set; }
            public string tipo { get; set; }
            public string int_genero { get; set; }
            public string dsc_imagen { get; set; }
            public string dsc_nome { get; set; }
            public string dsc_sinopse { get; set; }
            public string dsc_data_lancamento { get; set; }
            public string dsc_url_imdb { get; set; }
            public string dsc_nome_br { get; set; }
            public string soundex { get; set; }
            public string temporada { get; set; }
            public string id_usuario { get; set; }
            public string flg_liberado { get; set; }
            public string dsc_data_liberacao { get; set; }
            public string dsc_data { get; set; }
            public string dsc_metaphone_us { get; set; }
            public string dsc_metaphone_br { get; set; }
            public string episodios { get; set; }
            public string flg_seriado { get; set; }
            public string last_used { get; set; }
            public string deleted { get; set; }
        }

        public string _index { get; set; }
        public string _type { get; set; }
        public string _id { get; set; }
        public string _score { get; set; }
        public LegendasTVSource _source { get; set; }
    }
}
