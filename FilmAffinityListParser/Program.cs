using HtmlAgilityPack;
using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace FilmAffinityListParser
{
    class Program
    {
        private static readonly HttpClient client = new HttpClient();
        private const string API_URL = "http://radarr:7878/api/v3/movie/lookup";
        private static string API_KEY => GetRadarrApiKeyFromConfig();

        public static async Task Main(string[] args)
        {
            Console.WriteLine("FilmAffinity List Parser");
            Console.WriteLine("-------------------------");
            Console.WriteLine("Working path: " + Environment.GetEnvironmentVariable("INPUT_PATH"));

            string htmlFilePath = "/input/" + (args.Length > 0 ? args[0] : "filmaffinity_list.html");

            if (!File.Exists(htmlFilePath))
            {
                Console.WriteLine($"File not found: {htmlFilePath}");
                return;
            }

            var htmlDoc = new HtmlDocument();
            htmlDoc.Load(htmlFilePath);

            // Find movie rows
            var movieRows = htmlDoc.DocumentNode.SelectNodes("//table[@class='ml lists']/tr");

            if (movieRows is null)
            {
                Console.WriteLine("No movie data found in HTML file.");
                return;
            }

            // Extract movie titles with year
            var movieEntries = ExtractMovieEntriesFromRows(movieRows);

            Console.WriteLine($"Found {movieEntries.Count} movies in the list");
            (ConcurrentBag<MovieOutput> resultMovies, ConcurrentBag<string> notFoundMovies, ConcurrentBag<string> multipleMatchMovies) = await GetMovieProcessingResults(movieEntries);

            string baseOutputPath = "/output/movies_output.json";
            string outputPath = GetUniqueFilePath(baseOutputPath);
            string exportPath = Path.Combine("/output", Path.GetFileName(outputPath));

            // Ensure output directory exists
            Directory.CreateDirectory("/output");

            string jsonOutput = JsonSerializer.Serialize(resultMovies, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(exportPath, jsonOutput);

            // Write not found movies to a separate file
            string notFoundPath = GetUniqueFilePath("/output/movies_not_found.txt");
            File.WriteAllLines(notFoundPath, notFoundMovies);

            // Write multiple match movies to a separate file
            string multipleMatchPath = GetUniqueFilePath("/output/movies_multiple_matches.txt");
            File.WriteAllLines(multipleMatchPath, multipleMatchMovies);

            Console.WriteLine($"Processing complete. Saved {resultMovies.Count} movies to {exportPath}");
            Console.WriteLine($"Saved {notFoundMovies.Count} not found entries to {notFoundPath}");
            Console.WriteLine($"Saved {multipleMatchMovies.Count} multiple match entries to {multipleMatchPath}");
        }

        private static async Task<(ConcurrentBag<MovieOutput> resultMovies, ConcurrentBag<string> notFoundMovies, ConcurrentBag<string> multipleMatchMovies)> GetMovieProcessingResults(HashSet<string> movieEntries)
        {
            var resultMovies = new ConcurrentBag<MovieOutput>();
            var notFoundMovies = new ConcurrentBag<string>();
            var multipleMatchMovies = new ConcurrentBag<string>();

            // Limit degree of parallelism to avoid HttpClient exhaustion and timeouts
            int maxDegreeOfParallelism = 5;
            var throttler = new SemaphoreSlim(maxDegreeOfParallelism);

            var tasks = movieEntries.Select(async entry =>
            {
                await throttler.WaitAsync();
                try
                {
                    Console.WriteLine($"Processing: {entry}");

                    var apiResult = await GetMovieInfoFromApi(entry);

                    if (apiResult == null || !apiResult.Any())
                    {
                        // Try translating the title and searching again
                        string translatedEntry = await TranslateTitle(entry, "es", "en");
                        if (!string.Equals(translatedEntry, entry, StringComparison.OrdinalIgnoreCase))
                        {
                            apiResult = await GetMovieInfoFromApi(translatedEntry);
                        }
                    }

                    if (apiResult == null || !apiResult.Any())
                    {
                        Console.WriteLine($"No results found for: {entry}");
                        notFoundMovies.Add(entry);
                        return;
                    }
                    
                    // Handle multiple matches
                    MovieInfo selectedMovie;
                    if (apiResult.Count > 1)
                    {
                        Console.WriteLine($"More than 1 results found for: {entry}");
                        multipleMatchMovies.Add(entry);
                    }

                    selectedMovie = apiResult.First();

                    if (selectedMovie != null)
                    {
                        resultMovies.Add(new MovieOutput
                        {
                            Title = selectedMovie.Title,
                            Poster_url = GetPosterUrl(selectedMovie),
                            Imdb_id = selectedMovie.ImdbId,
                            Tmdb_id = selectedMovie.TmdbId
                        });
                    }
                }
                finally
                {
                    throttler.Release();
                }
            });

            await Task.WhenAll(tasks);
            return (resultMovies, notFoundMovies, multipleMatchMovies);
        }

        private static HashSet<string> ExtractMovieEntriesFromRows(HtmlNodeCollection movieRows)
        {
            var movieEntries = new HashSet<string>();
            foreach (var row in movieRows)
            {
                var titleCell = row.SelectSingleNode("td");
                if (titleCell != null)
                {
                    string fullTitle = titleCell.InnerText.Trim();
                    var match = Regex.Match(fullTitle, @"(.*?)\s*\((\d{4})\)");

                    if (match.Success)
                    {
                        movieEntries.Add(fullTitle);
                    }
                    else
                    {
                        Console.WriteLine($"Invalid format for row {fullTitle}");
                    }
                }
            }
            return movieEntries;
        }

        private static string GetUniqueFilePath(string basePath)
        {
            int counter = 1;
            string outputPath = basePath;
            while (File.Exists(outputPath))
            {
                string directory = Path.GetDirectoryName(basePath) ?? "";
                string fileNameWithoutExt = Path.GetFileNameWithoutExtension(basePath);
                string extension = Path.GetExtension(basePath);
                outputPath = Path.Combine(directory, $"{fileNameWithoutExt}_{counter}{extension}");
                counter++;
            }
            return outputPath;
        }
        private static async Task<List<MovieInfo>?> GetMovieInfoFromApi(string term)
        {
            try
            {
                int year = 0;
                var yearMatch = Regex.Match(term, @"\((\d{4})\)");
                if (yearMatch.Success && int.TryParse(yearMatch.Groups[1].Value, out int parsedYear))
                {
                    year = parsedYear;
                }

                var response = await client.GetFromJsonAsync<List<MovieInfo>>($"{API_URL}?term={Uri.EscapeDataString(term)}&apiKey={API_KEY}");

                // Order by popularity (descending), then by nulls last
                return response?
                    .Where(x => (!string.IsNullOrEmpty(x.ImdbId) || x.TmdbId.HasValue) && (x.Year == year || x.SecondaryYear == year || year == 0))
                    .OrderByDescending(x => x.Popularity ?? double.MinValue)
                    .ToList();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"API Error: {ex.Message}");
                return new List<MovieInfo>();
            }
        }
        private static string GetRadarrApiKeyFromConfig()
        {
            try
            {
                // Path to the Radarr config.xml file based on docker-compose volume mapping
                string configPath = "/data/radarr/config/config.xml"; 

                // To be tested: If running outside Docker, adjust the path
                if (!File.Exists(configPath))
                {
                    configPath = "../data/radarr/config/config.xml"; 
                }

                if (File.Exists(configPath))
                {
                    var xmlContent = File.ReadAllText(configPath);
                    var match = Regex.Match(xmlContent, @"<ApiKey>([^<]+)</ApiKey>");

                    if (match.Success && match.Groups.Count > 1)
                    {
                        return match.Groups[1].Value;
                    }
                }

                Console.WriteLine($"Could not find API key in config file at: {configPath}");
                return string.Empty;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error reading Radarr config: {ex.Message}");
                return string.Empty;
            }
        }
        private static MovieInfo? PromptUserToSelectMovie(List<MovieInfo> movies, string originalTitle)
        {
            Console.WriteLine($"Multiple matches found for '{originalTitle}'. Please select one:");

            for (int i = 0; i < movies.Count; i++)
            {
                var movie = movies[i];
                Console.WriteLine($"{i + 1}. {movie.Title} ({movie.Year}) - {movie.ImdbId}");
            }

            while (true)
            {
                Console.Write($"Enter selection (1-{movies.Count}) or 0 to skip: ");
                if (int.TryParse(Console.ReadLine(), out int selection))
                {
                    if (selection == 0)
                        return null; // User chose to skip

                    if (selection > 0 && selection <= movies.Count)
                        return movies[selection - 1];
                }

                Console.WriteLine("Invalid selection, please try again.");
            }
        }

        private static async Task<string> TranslateTitle(string title, string sourceLang = "es", string targetLang = "en")
        {
            const int maxRetries = 3;
            const int baseDelayMs = 1000;

            for (int attempt = 0; attempt < maxRetries; attempt++)
            {
                try
                {
                    using var httpClient = new HttpClient();
                    httpClient.Timeout = TimeSpan.FromSeconds(30);

                    // Test connection first
                    if (attempt == 0)
                    {
                        Console.WriteLine("Testing LibreTranslate connection...");
                        var healthResponse = await httpClient.GetAsync("http://libretranslate:5000/");
                        if (!healthResponse.IsSuccessStatusCode)
                        {
                            Console.WriteLine($"LibreTranslate health check failed: {healthResponse.StatusCode}");
                        }
                    }

                    var payload = new
                    {
                        q = title,
                        source = sourceLang,
                        target = targetLang,
                        format = "text"
                    };

                    Console.WriteLine($"Attempting to translate '{title}' (attempt {attempt + 1}/{maxRetries})");
                    var response = await httpClient.PostAsJsonAsync("http://libretranslate:5000/translate", payload);

                    if (response.IsSuccessStatusCode)
                    {
                        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
                        var translatedText = json.GetProperty("translatedText").GetString() ?? title;
                        Console.WriteLine($"Translation successful: '{title}' -> '{translatedText}'");
                        return translatedText;
                    }
                    else
                    {
                        Console.WriteLine($"Translation failed with status: {response.StatusCode}");
                        var errorContent = await response.Content.ReadAsStringAsync();
                        Console.WriteLine($"Error response: {errorContent}");
                    }
                }
                catch (HttpRequestException ex)
                {
                    Console.WriteLine($"HTTP request failed (attempt {attempt + 1}/{maxRetries}): {ex.Message}");
                    if (ex.InnerException != null)
                    {
                        Console.WriteLine($"Inner exception: {ex.InnerException.Message}");
                    }
                }
                catch (TaskCanceledException ex)
                {
                    Console.WriteLine($"Request timeout (attempt {attempt + 1}/{maxRetries}): {ex.Message}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Unexpected error during translation (attempt {attempt + 1}/{maxRetries}): {ex.Message}");
                }

                // Wait before retry (exponential backoff)
                if (attempt < maxRetries - 1)
                {
                    var delay = baseDelayMs * (int)Math.Pow(2, attempt);
                    Console.WriteLine($"Waiting {delay}ms before retry...");
                    await Task.Delay(delay);
                }
            }

            Console.WriteLine($"Translation failed after {maxRetries} attempts. Returning original title: '{title}'");
            return title;
        }
        private static string GetPosterUrl(MovieInfo movie)
        {
            var posterImage = movie.Images?.FirstOrDefault(img => img.CoverType == "poster");
            if (posterImage != null && !string.IsNullOrEmpty(posterImage.RemoteUrl))
            {
                return posterImage.RemoteUrl;
            }

            // If no poster found, use remotePoster if available
            if (!string.IsNullOrEmpty(movie.RemotePoster))
            {
                return movie.RemotePoster;
            }

            return string.Empty;
        }
    }

  public record MovieInfo
  {
    public string? Title { get; init; }
    public string? OriginalTitle { get; init; }
    public OriginalLanguage? OriginalLanguage { get; init; }
    public List<AlternateTitle>? AlternateTitles { get; init; }
    public int? Year { get; init; }
    public int? SecondaryYear { get; init; }
    public string? Overview { get; init; }
    public List<MovieImage>?  Images { get; init; }
    public string? RemotePoster { get; init; }
    public string? ImdbId { get; init; }
    public int? TmdbId { get; init; }
    public List<string>? Genres { get; init; }
    public double? Popularity { get; init; }
  }

  public record OriginalLanguage
  {
    public int? Id { get; init; }
    public string? Name { get; init; }
  }

  public record AlternateTitle
  {
    public string? SourceType { get; init; }
    public int? MovieMetadataId { get; init; }
    public string? Title { get; init; }
  }

  public record MovieImage
  {
    public string? CoverType { get; init; }
    public string? Url { get; init; }
    public string? RemoteUrl { get; init; }
  }

  public record MovieOutput
  {
    public string? Title { get; init; }
    public string? Poster_url { get; init; }
    public string? Imdb_id { get; init; }
    public int? Tmdb_id { get; init; }
  }
}