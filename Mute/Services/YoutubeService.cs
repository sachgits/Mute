﻿using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using JetBrains.Annotations;
using Newtonsoft.Json.Linq;
using Nito.AsyncEx;

namespace Mute.Services
{
    public class YoutubeService
    {
        [NotNull] private readonly YoutubeDlConfig _config;

        private readonly AsyncLock _mutex = new AsyncLock();

        public YoutubeService([NotNull] Configuration config, [NotNull] DatabaseService database)
        {
            _config = config.YoutubeDl;
        }

        [NotNull] public Uri CheckUrl([NotNull] string urlString)
        {
            //Sanity check that it's a well formed URL
            if (!Uri.TryCreate(urlString, UriKind.Absolute, out var url))
                throw new InvalidOperationException("Cannot download - not a valid URL");

            //Sanity check that the scheme is correct
            if (url.Scheme != "http" && url.Scheme != "https")
                throw new ArgumentException("URL scheme must be http(s)", nameof(urlString));

            //Extra check for escape characters in the URL
            if (urlString.Contains("\"") || urlString.Contains("\'"))
                throw new InvalidOperationException("Cannot download URL - it contains invalid characters");

            return url;
        }

        [ItemCanBeNull] public async Task<FileInfo> GetYoutubeAudio([NotNull] string youtubeUrl)
        {
            //Lock the mutex to ensure we only process one download at a time
            using (await _mutex.LockAsync())
                return await DownloadYoutube(CheckUrl(youtubeUrl), true);
        }

        [ItemCanBeNull] private async Task<FileInfo> DownloadYoutube([NotNull] Uri youtubeUrl, bool extractAudio)
        {
            //Build args
            var fileName = Guid.NewGuid().ToString();
            var downloadingLocation = Path.GetFullPath(Path.Combine(_config.InProgressDownloadFolder, fileName));
            var args = $"\"{youtubeUrl}\" --no-check-certificate --output \"{downloadingLocation}.`%(ext)s\" --quiet --no-warnings --no-playlist --write-info-json --limit-rate {_config.RateLimit ?? "2.5M"} --extract-audio --audio-format wav";

            Console.WriteLine(args);

            //Download to the in-progress-download folder
            try
            {
                await AsyncProcess.StartProcess(
                    Path.GetFullPath(_config.YoutubeDlBinaryPath), 
                    args,
                    Path.GetFullPath(_config.InProgressDownloadFolder)
                );
            }
            catch (TaskCanceledException)
            {
                return null;
            }

            //Find the completed download. It should be two files
            // <guid>.wav contains the audio
            // <guid>.json contains the metadata (including the video ID)
            var maybeCompleteFiles = Directory.GetFiles(Path.GetFullPath(_config.InProgressDownloadFolder), fileName + ".*").Select(f => new FileInfo(f)) .ToArray();

            //Early exit if it doesn't exist (download failed)
            if (maybeCompleteFiles.Length == 0)
                return null;

            //Find the two files we want
            var audioFile = maybeCompleteFiles.SingleOrDefault(f => f.Extension == ".wav");
            var metadataFile = maybeCompleteFiles.SingleOrDefault(f => f.Extension == ".json");

            //if one is null something went wrong, delete everything anbd early exit
            if (audioFile == null || metadataFile == null)
            {
                foreach (var maybeCompleteFile in maybeCompleteFiles)
                    maybeCompleteFile.Delete();
                return null;
            }
            else
            {
                //Find out the video ID from the metadata
                var metadata = JObject.Parse(await File.ReadAllTextAsync(metadataFile.FullName));
                var idToken = metadata["display_id"];
                var id = idToken.Value<string>();
                if (id == null)
                    return null;

                //Move to completed folder, with the ID of the video as the name
                var finalLocation = new FileInfo(Path.Combine(_config.CompleteDownloadFolder, id + ".wav"));
                if (finalLocation.Exists)
                    audioFile.Delete();
                else
                    audioFile.MoveTo(finalLocation.FullName);

                //Delete temp files
                metadataFile.Delete();

                //return final audio file
                return finalLocation;
            }
        }
    }
}