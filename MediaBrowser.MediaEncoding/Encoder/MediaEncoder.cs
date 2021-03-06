using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Session;
using MediaBrowser.MediaEncoding.Probing;
using MediaBrowser.Model.Dlna;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.MediaInfo;
using MediaBrowser.Model.Serialization;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommonIO;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Extensions;
using MediaBrowser.Common.Net;

namespace MediaBrowser.MediaEncoding.Encoder
{
    /// <summary>
    /// Class MediaEncoder
    /// </summary>
    public class MediaEncoder : IMediaEncoder, IDisposable
    {
        /// <summary>
        /// The _logger
        /// </summary>
        private readonly ILogger _logger;

        /// <summary>
        /// Gets the json serializer.
        /// </summary>
        /// <value>The json serializer.</value>
        private readonly IJsonSerializer _jsonSerializer;

        /// <summary>
        /// The _thumbnail resource pool
        /// </summary>
        private readonly SemaphoreSlim _thumbnailResourcePool = new SemaphoreSlim(1, 1);

        /// <summary>
        /// The video image resource pool
        /// </summary>
        private readonly SemaphoreSlim _videoImageResourcePool = new SemaphoreSlim(1, 1);

        /// <summary>
        /// The audio image resource pool
        /// </summary>
        private readonly SemaphoreSlim _audioImageResourcePool = new SemaphoreSlim(2, 2);

        /// <summary>
        /// The FF probe resource pool
        /// </summary>
        private readonly SemaphoreSlim _ffProbeResourcePool = new SemaphoreSlim(2, 2);

        public string FFMpegPath { get; private set; }

        public string FFProbePath { get; private set; }

        protected readonly IServerConfigurationManager ConfigurationManager;
        protected readonly IFileSystem FileSystem;
        protected readonly ILiveTvManager LiveTvManager;
        protected readonly IIsoManager IsoManager;
        protected readonly ILibraryManager LibraryManager;
        protected readonly IChannelManager ChannelManager;
        protected readonly ISessionManager SessionManager;
        protected readonly Func<ISubtitleEncoder> SubtitleEncoder;
        protected readonly Func<IMediaSourceManager> MediaSourceManager;
        private readonly IHttpClient _httpClient;
        private readonly IZipClient _zipClient;

        private readonly List<ProcessWrapper> _runningProcesses = new List<ProcessWrapper>();
        private readonly bool _hasExternalEncoder;

        public MediaEncoder(ILogger logger, IJsonSerializer jsonSerializer, string ffMpegPath, string ffProbePath, bool hasExternalEncoder, IServerConfigurationManager configurationManager, IFileSystem fileSystem, ILiveTvManager liveTvManager, IIsoManager isoManager, ILibraryManager libraryManager, IChannelManager channelManager, ISessionManager sessionManager, Func<ISubtitleEncoder> subtitleEncoder, Func<IMediaSourceManager> mediaSourceManager, IHttpClient httpClient, IZipClient zipClient)
        {
            _logger = logger;
            _jsonSerializer = jsonSerializer;
            ConfigurationManager = configurationManager;
            FileSystem = fileSystem;
            LiveTvManager = liveTvManager;
            IsoManager = isoManager;
            LibraryManager = libraryManager;
            ChannelManager = channelManager;
            SessionManager = sessionManager;
            SubtitleEncoder = subtitleEncoder;
            MediaSourceManager = mediaSourceManager;
            _httpClient = httpClient;
            _zipClient = zipClient;
            FFProbePath = ffProbePath;
            FFMpegPath = ffMpegPath;

            _hasExternalEncoder = hasExternalEncoder;
        }

        public string EncoderLocationType
        {
            get
            {
                if (_hasExternalEncoder)
                {
                    return "External";
                }

                if (string.IsNullOrWhiteSpace(FFMpegPath))
                {
                    return null;
                }

                if (IsSystemInstalledPath(FFMpegPath))
                {
                    return "System";
                }

                return "Custom";
            }
        }

        private bool IsSystemInstalledPath(string path)
        {
            if (path.IndexOf("/", StringComparison.Ordinal) == -1 && path.IndexOf("\\", StringComparison.Ordinal) == -1)
            {
                return true;
            }

            return false;
        }

        public async Task Init()
        {
            InitPaths();

            if (!string.IsNullOrWhiteSpace(FFMpegPath))
            {
                var result = new EncoderValidator(_logger).Validate(FFMpegPath);

                SetAvailableDecoders(result.Item1);
                SetAvailableEncoders(result.Item2);

                if (Environment.OSVersion.Platform == PlatformID.Win32NT)
                {
                    var directory = Path.GetDirectoryName(FFMpegPath);

                    if (FileSystem.ContainsSubPath(ConfigurationManager.ApplicationPaths.ProgramDataPath, directory))
                    {
                        await new FontConfigLoader(_httpClient, ConfigurationManager.ApplicationPaths, _logger, _zipClient,
                                FileSystem).DownloadFonts(directory).ConfigureAwait(false);
                    }
                }
            }
        }

        private void InitPaths()
        {
            ConfigureEncoderPaths();

            if (_hasExternalEncoder)
            {
                LogPaths();
                return;
            }

            // If the path was passed in, save it into config now.
            var encodingOptions = GetEncodingOptions();
            var appPath = encodingOptions.EncoderAppPath;

            var valueToSave = FFMpegPath;

            if (!string.IsNullOrWhiteSpace(valueToSave))
            {
                // if using system variable, don't save this.
                if (IsSystemInstalledPath(valueToSave))
                {
                    valueToSave = null;
                }
            }

            if (!string.Equals(valueToSave, appPath, StringComparison.Ordinal))
            {
                encodingOptions.EncoderAppPath = valueToSave;
                ConfigurationManager.SaveConfiguration("encoding", encodingOptions);
            }
        }

        public async Task UpdateEncoderPath(string path, string pathType)
        {
            if (_hasExternalEncoder)
            {
                return;
            }

            Tuple<string, string> newPaths;

            if (string.Equals(pathType, "system", StringComparison.OrdinalIgnoreCase))
            {
                path = "ffmpeg";

                newPaths = TestForInstalledVersions();
            }
            else if (string.Equals(pathType, "custom", StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(path))
                {
                    throw new ArgumentNullException("path");
                }

                if (!File.Exists(path) && !Directory.Exists(path))
                {
                    throw new ResourceNotFoundException();
                }
                newPaths = GetEncoderPaths(path);
            }
            else
            {
                throw new ArgumentException("Unexpected pathType value");
            }

            if (string.IsNullOrWhiteSpace(newPaths.Item1))
            {
                throw new ResourceNotFoundException("ffmpeg not found");
            }
            if (string.IsNullOrWhiteSpace(newPaths.Item2))
            {
                throw new ResourceNotFoundException("ffprobe not found");
            }

            var config = GetEncodingOptions();
            config.EncoderAppPath = path;
            ConfigurationManager.SaveConfiguration("encoding", config);

            Init();
        }

        private void ConfigureEncoderPaths()
        {
            if (_hasExternalEncoder)
            {
                return;
            }

            var appPath = GetEncodingOptions().EncoderAppPath;

            if (string.IsNullOrWhiteSpace(appPath))
            {
                appPath = Path.Combine(ConfigurationManager.ApplicationPaths.ProgramDataPath, "ffmpeg");
            }

            var newPaths = GetEncoderPaths(appPath);
            if (string.IsNullOrWhiteSpace(newPaths.Item1) || string.IsNullOrWhiteSpace(newPaths.Item2))
            {
                newPaths = TestForInstalledVersions();
            }

            if (!string.IsNullOrWhiteSpace(newPaths.Item1) && !string.IsNullOrWhiteSpace(newPaths.Item2))
            {
                FFMpegPath = newPaths.Item1;
                FFProbePath = newPaths.Item2;
            }

            LogPaths();
        }

        private Tuple<string, string> GetEncoderPaths(string configuredPath)
        {
            var appPath = configuredPath;

            if (!string.IsNullOrWhiteSpace(appPath))
            {
                if (Directory.Exists(appPath))
                {
                    return GetPathsFromDirectory(appPath);
                }

                if (File.Exists(appPath))
                {
                    return new Tuple<string, string>(appPath, GetProbePathFromEncoderPath(appPath));
                }
            }

            return new Tuple<string, string>(null, null);
        }

        private Tuple<string, string> TestForInstalledVersions()
        {
            string encoderPath = null;
            string probePath = null;

            if (TestSystemInstalled("ffmpeg"))
            {
                encoderPath = "ffmpeg";
            }
            if (TestSystemInstalled("ffprobe"))
            {
                probePath = "ffprobe";
            }

            return new Tuple<string, string>(encoderPath, probePath);
        }

        private bool TestSystemInstalled(string app)
        {
            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = app,
                    Arguments = "-v",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                    ErrorDialog = false
                };

                using (var process = Process.Start(startInfo))
                {
                    process.WaitForExit();
                }

                _logger.Debug("System app installed: " + app);
                return true;
            }
            catch
            {
                _logger.Debug("System app not installed: " + app);
                return false;
            }
        }

        private Tuple<string, string> GetPathsFromDirectory(string path)
        {
            // Since we can't predict the file extension, first try directly within the folder 
            // If that doesn't pan out, then do a recursive search
            var files = Directory.GetFiles(path);

            var ffmpegPath = files.FirstOrDefault(i => string.Equals(Path.GetFileNameWithoutExtension(i), "ffmpeg", StringComparison.OrdinalIgnoreCase));
            var ffprobePath = files.FirstOrDefault(i => string.Equals(Path.GetFileNameWithoutExtension(i), "ffprobe", StringComparison.OrdinalIgnoreCase));

            if (string.IsNullOrWhiteSpace(ffmpegPath) || !File.Exists(ffmpegPath))
            {
                files = Directory.GetFiles(path, "*", SearchOption.AllDirectories);

                ffmpegPath = files.FirstOrDefault(i => string.Equals(Path.GetFileNameWithoutExtension(i), "ffmpeg", StringComparison.OrdinalIgnoreCase));

                if (!string.IsNullOrWhiteSpace(ffmpegPath))
                {
                    ffprobePath = GetProbePathFromEncoderPath(ffmpegPath);
                }
            }

            return new Tuple<string, string>(ffmpegPath, ffprobePath);
        }

        private string GetProbePathFromEncoderPath(string appPath)
        {
            return Directory.GetFiles(Path.GetDirectoryName(appPath))
                .FirstOrDefault(i => string.Equals(Path.GetFileNameWithoutExtension(i), "ffprobe", StringComparison.OrdinalIgnoreCase));
        }

        private void LogPaths()
        {
            _logger.Info("FFMpeg: {0}", FFMpegPath ?? "not found");
            _logger.Info("FFProbe: {0}", FFProbePath ?? "not found");
        }

        private EncodingOptions GetEncodingOptions()
        {
            return ConfigurationManager.GetConfiguration<EncodingOptions>("encoding");
        }

        private List<string> _encoders = new List<string>();
        public void SetAvailableEncoders(List<string> list)
        {
            _encoders = list.ToList();
            //_logger.Info("Supported encoders: {0}", string.Join(",", list.ToArray()));
        }

        private List<string> _decoders = new List<string>();
        public void SetAvailableDecoders(List<string> list)
        {
            _decoders = list.ToList();
            //_logger.Info("Supported decoders: {0}", string.Join(",", list.ToArray()));
        }

        public bool SupportsEncoder(string decoder)
        {
            return _encoders.Contains(decoder, StringComparer.OrdinalIgnoreCase);
        }

        public bool SupportsDecoder(string decoder)
        {
            return _decoders.Contains(decoder, StringComparer.OrdinalIgnoreCase);
        }

        public bool CanEncodeToAudioCodec(string codec)
        {
            if (string.Equals(codec, "opus", StringComparison.OrdinalIgnoreCase))
            {
                codec = "libopus";
            }
            else if (string.Equals(codec, "mp3", StringComparison.OrdinalIgnoreCase))
            {
                codec = "libmp3lame";
            }

            return SupportsEncoder(codec);
        }

        /// <summary>
        /// Gets the encoder path.
        /// </summary>
        /// <value>The encoder path.</value>
        public string EncoderPath
        {
            get { return FFMpegPath; }
        }

        /// <summary>
        /// Gets the media info.
        /// </summary>
        /// <param name="request">The request.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>Task.</returns>
        public Task<MediaInfo> GetMediaInfo(MediaInfoRequest request, CancellationToken cancellationToken)
        {
            var extractChapters = request.MediaType == DlnaProfileType.Video && request.ExtractChapters;

            var inputFiles = MediaEncoderHelpers.GetInputArgument(FileSystem, request.InputPath, request.Protocol, request.MountedIso, request.PlayableStreamFileNames);

            return GetMediaInfoInternal(GetInputArgument(inputFiles, request.Protocol), request.InputPath, request.Protocol, extractChapters,
                GetProbeSizeArgument(inputFiles, request.Protocol), request.MediaType == DlnaProfileType.Audio, request.VideoType, cancellationToken);
        }

        /// <summary>
        /// Gets the input argument.
        /// </summary>
        /// <param name="inputFiles">The input files.</param>
        /// <param name="protocol">The protocol.</param>
        /// <returns>System.String.</returns>
        /// <exception cref="System.ArgumentException">Unrecognized InputType</exception>
        public string GetInputArgument(string[] inputFiles, MediaProtocol protocol)
        {
            return EncodingUtils.GetInputArgument(inputFiles.ToList(), protocol);
        }

        /// <summary>
        /// Gets the probe size argument.
        /// </summary>
        /// <param name="inputFiles">The input files.</param>
        /// <param name="protocol">The protocol.</param>
        /// <returns>System.String.</returns>
        public string GetProbeSizeArgument(string[] inputFiles, MediaProtocol protocol)
        {
            return EncodingUtils.GetProbeSizeArgument(inputFiles.Length > 1);
        }

        /// <summary>
        /// Gets the media info internal.
        /// </summary>
        /// <param name="inputPath">The input path.</param>
        /// <param name="primaryPath">The primary path.</param>
        /// <param name="protocol">The protocol.</param>
        /// <param name="extractChapters">if set to <c>true</c> [extract chapters].</param>
        /// <param name="probeSizeArgument">The probe size argument.</param>
        /// <param name="isAudio">if set to <c>true</c> [is audio].</param>
        /// <param name="videoType">Type of the video.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>Task{MediaInfoResult}.</returns>
        /// <exception cref="System.ApplicationException">ffprobe failed - streams and format are both null.</exception>
        private async Task<MediaInfo> GetMediaInfoInternal(string inputPath,
            string primaryPath,
            MediaProtocol protocol,
            bool extractChapters,
            string probeSizeArgument,
            bool isAudio,
            VideoType videoType,
            CancellationToken cancellationToken)
        {
            var args = extractChapters
                ? "{0} -i {1} -threads 0 -v info -print_format json -show_streams -show_chapters -show_format"
                : "{0} -i {1} -threads 0 -v info -print_format json -show_streams -show_format";

            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    CreateNoWindow = true,
                    UseShellExecute = false,

                    // Must consume both or ffmpeg may hang due to deadlocks. See comments below.   
                    RedirectStandardOutput = true,
                    //RedirectStandardError = true,
                    RedirectStandardInput = true,
                    FileName = FFProbePath,
                    Arguments = string.Format(args,
                    probeSizeArgument, inputPath).Trim(),

                    WindowStyle = ProcessWindowStyle.Hidden,
                    ErrorDialog = false
                },

                EnableRaisingEvents = true
            };

            _logger.Debug("{0} {1}", process.StartInfo.FileName, process.StartInfo.Arguments);

            using (var processWrapper = new ProcessWrapper(process, this, _logger))
            {
                await _ffProbeResourcePool.WaitAsync(cancellationToken).ConfigureAwait(false);

                try
                {
                    StartProcess(processWrapper);
                }
                catch (Exception ex)
                {
                    _ffProbeResourcePool.Release();

                    _logger.ErrorException("Error starting ffprobe", ex);

                    throw;
                }

                try
                {
                    //process.BeginErrorReadLine();

                    var result = _jsonSerializer.DeserializeFromStream<InternalMediaInfoResult>(process.StandardOutput.BaseStream);

                    if (result.streams == null && result.format == null)
                    {
                        throw new ApplicationException("ffprobe failed - streams and format are both null.");
                    }

                    if (result.streams != null)
                    {
                        // Normalize aspect ratio if invalid
                        foreach (var stream in result.streams)
                        {
                            if (string.Equals(stream.display_aspect_ratio, "0:1", StringComparison.OrdinalIgnoreCase))
                            {
                                stream.display_aspect_ratio = string.Empty;
                            }
                            if (string.Equals(stream.sample_aspect_ratio, "0:1", StringComparison.OrdinalIgnoreCase))
                            {
                                stream.sample_aspect_ratio = string.Empty;
                            }
                        }
                    }

                    var mediaInfo = new ProbeResultNormalizer(_logger, FileSystem).GetMediaInfo(result, videoType, isAudio, primaryPath, protocol);

                    var videoStream = mediaInfo.MediaStreams.FirstOrDefault(i => i.Type == MediaStreamType.Video);

                    if (videoStream != null)
                    {
                        var isInterlaced = await DetectInterlaced(mediaInfo, videoStream, inputPath, probeSizeArgument).ConfigureAwait(false);

                        if (isInterlaced)
                        {
                            videoStream.IsInterlaced = true;
                        }
                    }

                    return mediaInfo;
                }
                catch
                {
                    StopProcess(processWrapper, 100, true);

                    throw;
                }
                finally
                {
                    _ffProbeResourcePool.Release();
                }
            }
        }

        private async Task<bool> DetectInterlaced(MediaSourceInfo video, MediaStream videoStream, string inputPath, string probeSizeArgument)
        {
            if (video.Protocol != MediaProtocol.File)
            {
                return false;
            }

            var formats = (video.Container ?? string.Empty).Split(',').ToList();
            var enableInterlacedDection = formats.Contains("vob", StringComparer.OrdinalIgnoreCase) ||
                                          formats.Contains("m2ts", StringComparer.OrdinalIgnoreCase) ||
                                          formats.Contains("ts", StringComparer.OrdinalIgnoreCase) ||
                                          formats.Contains("mpegts", StringComparer.OrdinalIgnoreCase) ||
                                          formats.Contains("wtv", StringComparer.OrdinalIgnoreCase);

            // If it's mpeg based, assume true
            if ((videoStream.Codec ?? string.Empty).IndexOf("mpeg", StringComparison.OrdinalIgnoreCase) != -1)
            {
                if (enableInterlacedDection)
                {
                    return true;
                }
            }
            else
            {
                // If the video codec is not some form of mpeg, then take a shortcut and limit this to containers that are likely to have interlaced content
                if (!enableInterlacedDection)
                {
                    return false;
                }
            }

            var args = "{0} -i {1} -map 0:v:{2} -an -filter:v idet -frames:v 500 -an -f null /dev/null";

            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    CreateNoWindow = true,
                    UseShellExecute = false,

                    // Must consume both or ffmpeg may hang due to deadlocks. See comments below.   
                    //RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    RedirectStandardInput = true,
                    FileName = FFMpegPath,
                    Arguments = string.Format(args, probeSizeArgument, inputPath, videoStream.Index.ToString(CultureInfo.InvariantCulture)).Trim(),

                    WindowStyle = ProcessWindowStyle.Hidden,
                    ErrorDialog = false
                },

                EnableRaisingEvents = true
            };

            _logger.Debug("{0} {1}", process.StartInfo.FileName, process.StartInfo.Arguments);
            var idetFoundInterlaced = false;

            using (var processWrapper = new ProcessWrapper(process, this, _logger))
            {
                try
                {
                    StartProcess(processWrapper);
                }
                catch (Exception ex)
                {
                    _logger.ErrorException("Error starting ffprobe", ex);

                    throw;
                }

                try
                {
                    //process.BeginOutputReadLine();

                    using (var reader = new StreamReader(process.StandardError.BaseStream))
                    {
                        while (!reader.EndOfStream)
                        {
                            var line = await reader.ReadLineAsync().ConfigureAwait(false);

                            if (line.StartsWith("[Parsed_idet", StringComparison.OrdinalIgnoreCase))
                            {
                                var idetResult = AnalyzeIdetResult(line);

                                if (idetResult.HasValue)
                                {
                                    if (!idetResult.Value)
                                    {
                                        return false;
                                    }

                                    idetFoundInterlaced = true;
                                }
                            }
                        }
                    }

                }
                catch
                {
                    StopProcess(processWrapper, 100, true);

                    throw;
                }
            }

            return idetFoundInterlaced;
        }

        private bool? AnalyzeIdetResult(string line)
        {
            // As you can see, the filter only guessed one frame as progressive. 
            // Results like this are pretty typical. So if less than 30% of the detections are in the "Undetermined" category, then I only consider the video to be interlaced if at least 65% of the identified frames are in either the TFF or BFF category. 
            // In this case (310 + 311)/(622) = 99.8% which is well over the 65% metric. I may refine that number with more testing but I honestly do not believe I will need to.
            // http://awel.domblogger.net/videoTranscode/interlace.html
            var index = line.IndexOf("detection:", StringComparison.OrdinalIgnoreCase);

            if (index == -1)
            {
                return null;
            }

            line = line.Substring(index).Trim();
            var parts = line.Split(' ').Where(i => !string.IsNullOrWhiteSpace(i)).Select(i => i.Trim()).ToList();

            if (parts.Count < 2)
            {
                return null;
            }
            double tff = 0;
            double bff = 0;
            double progressive = 0;
            double undetermined = 0;
            double total = 0;

            for (var i = 0; i < parts.Count - 1; i++)
            {
                var part = parts[i];

                if (string.Equals(part, "tff:", StringComparison.OrdinalIgnoreCase))
                {
                    tff = GetNextPart(parts, i);
                    total += tff;
                }
                else if (string.Equals(part, "bff:", StringComparison.OrdinalIgnoreCase))
                {
                    bff = GetNextPart(parts, i);
                    total += tff;
                }
                else if (string.Equals(part, "progressive:", StringComparison.OrdinalIgnoreCase))
                {
                    progressive = GetNextPart(parts, i);
                    total += progressive;
                }
                else if (string.Equals(part, "undetermined:", StringComparison.OrdinalIgnoreCase))
                {
                    undetermined = GetNextPart(parts, i);
                    total += undetermined;
                }
            }

            if (total == 0)
            {
                return null;
            }

            if ((undetermined / total) >= .3)
            {
                return false;
            }

            if (((tff + bff) / total) >= .4)
            {
                return true;
            }

            return false;
        }

        private int GetNextPart(List<string> parts, int index)
        {
            var next = parts[index + 1];

            int value;
            if (int.TryParse(next, NumberStyles.Any, CultureInfo.InvariantCulture, out value))
            {
                return value;
            }
            return 0;
        }

        /// <summary>
        /// The us culture
        /// </summary>
        protected readonly CultureInfo UsCulture = new CultureInfo("en-US");

        public Task<string> ExtractAudioImage(string path, int? imageStreamIndex, CancellationToken cancellationToken)
        {
            return ExtractImage(new[] { path }, imageStreamIndex, MediaProtocol.File, true, null, null, cancellationToken);
        }

        public Task<string> ExtractVideoImage(string[] inputFiles, MediaProtocol protocol, Video3DFormat? threedFormat, TimeSpan? offset, CancellationToken cancellationToken)
        {
            return ExtractImage(inputFiles, null, protocol, false, threedFormat, offset, cancellationToken);
        }

        public Task<string> ExtractVideoImage(string[] inputFiles, MediaProtocol protocol, int? imageStreamIndex, CancellationToken cancellationToken)
        {
            return ExtractImage(inputFiles, imageStreamIndex, protocol, false, null, null, cancellationToken);
        }

        private async Task<string> ExtractImage(string[] inputFiles, int? imageStreamIndex, MediaProtocol protocol, bool isAudio,
            Video3DFormat? threedFormat, TimeSpan? offset, CancellationToken cancellationToken)
        {
            var resourcePool = isAudio ? _audioImageResourcePool : _videoImageResourcePool;

            var inputArgument = GetInputArgument(inputFiles, protocol);

            if (isAudio)
            {
                if (imageStreamIndex.HasValue && imageStreamIndex.Value > 0)
                {
                    // It seems for audio files we need to subtract 1 (for the audio stream??)
                    imageStreamIndex = imageStreamIndex.Value - 1;
                }
            }
            else
            {
                try
                {
                    return await ExtractImageInternal(inputArgument, imageStreamIndex, protocol, threedFormat, offset, true, resourcePool, cancellationToken).ConfigureAwait(false);
                }
                catch (ArgumentException)
                {
                    throw;
                }
                catch
                {
                    _logger.Error("I-frame image extraction failed, will attempt standard way. Input: {0}", inputArgument);
                }
            }

            return await ExtractImageInternal(inputArgument, imageStreamIndex, protocol, threedFormat, offset, false, resourcePool, cancellationToken).ConfigureAwait(false);
        }

        private async Task<string> ExtractImageInternal(string inputPath, int? imageStreamIndex, MediaProtocol protocol, Video3DFormat? threedFormat, TimeSpan? offset, bool useIFrame, SemaphoreSlim resourcePool, CancellationToken cancellationToken)
        {
            if (string.IsNullOrEmpty(inputPath))
            {
                throw new ArgumentNullException("inputPath");
            }

            var tempExtractPath = Path.Combine(ConfigurationManager.ApplicationPaths.TempDirectory, Guid.NewGuid() + ".jpg");
            Directory.CreateDirectory(Path.GetDirectoryName(tempExtractPath));

            // apply some filters to thumbnail extracted below (below) crop any black lines that we made and get the correct ar then scale to width 600. 
            // This filter chain may have adverse effects on recorded tv thumbnails if ar changes during presentation ex. commercials @ diff ar
            var vf = "scale=600:trunc(600/dar/2)*2";

            if (threedFormat.HasValue)
            {
                switch (threedFormat.Value)
                {
                    case Video3DFormat.HalfSideBySide:
                        vf = "crop=iw/2:ih:0:0,scale=(iw*2):ih,setdar=dar=a,crop=min(iw\\,ih*dar):min(ih\\,iw/dar):(iw-min(iw\\,iw*sar))/2:(ih - min (ih\\,ih/sar))/2,setsar=sar=1,scale=600:trunc(600/dar/2)*2";
                        // hsbs crop width in half,scale to correct size, set the display aspect,crop out any black bars we may have made the scale width to 600. Work out the correct height based on the display aspect it will maintain the aspect where -1 in this case (3d) may not.
                        break;
                    case Video3DFormat.FullSideBySide:
                        vf = "crop=iw/2:ih:0:0,setdar=dar=a,crop=min(iw\\,ih*dar):min(ih\\,iw/dar):(iw-min(iw\\,iw*sar))/2:(ih - min (ih\\,ih/sar))/2,setsar=sar=1,scale=600:trunc(600/dar/2)*2";
                        //fsbs crop width in half,set the display aspect,crop out any black bars we may have made the scale width to 600.
                        break;
                    case Video3DFormat.HalfTopAndBottom:
                        vf = "crop=iw:ih/2:0:0,scale=(iw*2):ih),setdar=dar=a,crop=min(iw\\,ih*dar):min(ih\\,iw/dar):(iw-min(iw\\,iw*sar))/2:(ih - min (ih\\,ih/sar))/2,setsar=sar=1,scale=600:trunc(600/dar/2)*2";
                        //htab crop heigh in half,scale to correct size, set the display aspect,crop out any black bars we may have made the scale width to 600
                        break;
                    case Video3DFormat.FullTopAndBottom:
                        vf = "crop=iw:ih/2:0:0,setdar=dar=a,crop=min(iw\\,ih*dar):min(ih\\,iw/dar):(iw-min(iw\\,iw*sar))/2:(ih - min (ih\\,ih/sar))/2,setsar=sar=1,scale=600:trunc(600/dar/2)*2";
                        // ftab crop heigt in half, set the display aspect,crop out any black bars we may have made the scale width to 600
                        break;
                    default:
                        break;
                }
            }

            var mapArg = imageStreamIndex.HasValue ? (" -map 0:v:" + imageStreamIndex.Value.ToString(CultureInfo.InvariantCulture)) : string.Empty;

            // Use ffmpeg to sample 100 (we can drop this if required using thumbnail=50 for 50 frames) frames and pick the best thumbnail. Have a fall back just in case.
            var args = useIFrame ? string.Format("-i {0}{3} -threads 1 -v quiet -vframes 1 -vf \"{2},thumbnail=30\" -f image2 \"{1}\"", inputPath, tempExtractPath, vf, mapArg) :
                string.Format("-i {0}{3} -threads 1 -v quiet -vframes 1 -vf \"{2}\" -f image2 \"{1}\"", inputPath, tempExtractPath, vf, mapArg);

            var probeSize = GetProbeSizeArgument(new[] { inputPath }, protocol);

            if (!string.IsNullOrEmpty(probeSize))
            {
                args = probeSize + " " + args;
            }

            if (offset.HasValue)
            {
                args = string.Format("-ss {0} ", GetTimeParameter(offset.Value)) + args;
            }

            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    FileName = FFMpegPath,
                    Arguments = args,
                    WindowStyle = ProcessWindowStyle.Hidden,
                    ErrorDialog = false,
                    RedirectStandardInput = true
                }
            };

            _logger.Debug("{0} {1}", process.StartInfo.FileName, process.StartInfo.Arguments);

            using (var processWrapper = new ProcessWrapper(process, this, _logger))
            {
                await resourcePool.WaitAsync(cancellationToken).ConfigureAwait(false);

                bool ranToCompletion;

                try
                {
                    StartProcess(processWrapper);

                    ranToCompletion = process.WaitForExit(10000);

                    if (!ranToCompletion)
                    {
                        StopProcess(processWrapper, 1000, false);
                    }

                }
                finally
                {
                    resourcePool.Release();
                }

                var exitCode = ranToCompletion ? processWrapper.ExitCode ?? 0 : -1;
                var file = new FileInfo(tempExtractPath);

                if (exitCode == -1 || !file.Exists || file.Length == 0)
                {
                    var msg = string.Format("ffmpeg image extraction failed for {0}", inputPath);

                    _logger.Error(msg);

                    throw new ApplicationException(msg);
                }

                return tempExtractPath;
            }
        }

        public string GetTimeParameter(long ticks)
        {
            var time = TimeSpan.FromTicks(ticks);

            return GetTimeParameter(time);
        }

        public string GetTimeParameter(TimeSpan time)
        {
            return time.ToString(@"hh\:mm\:ss\.fff", UsCulture);
        }

        public async Task ExtractVideoImagesOnInterval(string[] inputFiles,
            MediaProtocol protocol,
            Video3DFormat? threedFormat,
            TimeSpan interval,
            string targetDirectory,
            string filenamePrefix,
            int? maxWidth,
            CancellationToken cancellationToken)
        {
            var resourcePool = _thumbnailResourcePool;

            var inputArgument = GetInputArgument(inputFiles, protocol);

            var vf = "fps=fps=1/" + interval.TotalSeconds.ToString(UsCulture);

            if (maxWidth.HasValue)
            {
                var maxWidthParam = maxWidth.Value.ToString(UsCulture);

                vf += string.Format(",scale=min(iw\\,{0}):trunc(ow/dar/2)*2", maxWidthParam);
            }

            FileSystem.CreateDirectory(targetDirectory);
            var outputPath = Path.Combine(targetDirectory, filenamePrefix + "%05d.jpg");

            var args = string.Format("-i {0} -threads 1 -v quiet -vf \"{2}\" -f image2 \"{1}\"", inputArgument, outputPath, vf);

            var probeSize = GetProbeSizeArgument(new[] { inputArgument }, protocol);

            if (!string.IsNullOrEmpty(probeSize))
            {
                args = probeSize + " " + args;
            }

            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    FileName = FFMpegPath,
                    Arguments = args,
                    WindowStyle = ProcessWindowStyle.Hidden,
                    ErrorDialog = false,
                    RedirectStandardInput = true
                }
            };

            _logger.Info(process.StartInfo.FileName + " " + process.StartInfo.Arguments);

            await resourcePool.WaitAsync(cancellationToken).ConfigureAwait(false);

            bool ranToCompletion = false;

            using (var processWrapper = new ProcessWrapper(process, this, _logger))
            {
                try
                {
                    StartProcess(processWrapper);

                    // Need to give ffmpeg enough time to make all the thumbnails, which could be a while,
                    // but we still need to detect if the process hangs.
                    // Making the assumption that as long as new jpegs are showing up, everything is good.

                    bool isResponsive = true;
                    int lastCount = 0;

                    while (isResponsive)
                    {
                        if (process.WaitForExit(30000))
                        {
                            ranToCompletion = true;
                            break;
                        }

                        cancellationToken.ThrowIfCancellationRequested();

                        var jpegCount = Directory.GetFiles(targetDirectory)
                            .Count(i => string.Equals(Path.GetExtension(i), ".jpg", StringComparison.OrdinalIgnoreCase));

                        isResponsive = (jpegCount > lastCount);
                        lastCount = jpegCount;
                    }

                    if (!ranToCompletion)
                    {
                        StopProcess(processWrapper, 1000, false);
                    }
                }
                finally
                {
                    resourcePool.Release();
                }

                var exitCode = ranToCompletion ? processWrapper.ExitCode ?? 0 : -1;

                if (exitCode == -1)
                {
                    var msg = string.Format("ffmpeg image extraction failed for {0}", inputArgument);

                    _logger.Error(msg);

                    throw new ApplicationException(msg);
                }
            }
        }

        public async Task<string> EncodeAudio(EncodingJobOptions options,
            IProgress<double> progress,
            CancellationToken cancellationToken)
        {
            var job = await new AudioEncoder(this,
                _logger,
                ConfigurationManager,
                FileSystem,
                IsoManager,
                LibraryManager,
                SessionManager,
                SubtitleEncoder(),
                MediaSourceManager())
                .Start(options, progress, cancellationToken).ConfigureAwait(false);

            await job.TaskCompletionSource.Task.ConfigureAwait(false);

            return job.OutputFilePath;
        }

        public async Task<string> EncodeVideo(EncodingJobOptions options,
            IProgress<double> progress,
            CancellationToken cancellationToken)
        {
            var job = await new VideoEncoder(this,
                _logger,
                ConfigurationManager,
                FileSystem,
                IsoManager,
                LibraryManager,
                SessionManager,
                SubtitleEncoder(),
                MediaSourceManager())
                .Start(options, progress, cancellationToken).ConfigureAwait(false);

            await job.TaskCompletionSource.Task.ConfigureAwait(false);

            return job.OutputFilePath;
        }

        private void StartProcess(ProcessWrapper process)
        {
            process.Process.Start();

            lock (_runningProcesses)
            {
                _runningProcesses.Add(process);
            }
        }
        private void StopProcess(ProcessWrapper process, int waitTimeMs, bool enableForceKill)
        {
            try
            {
                _logger.Info("Killing ffmpeg process");

                try
                {
                    process.Process.StandardInput.WriteLine("q");
                }
                catch (Exception)
                {
                    _logger.Error("Error sending q command to process");
                }

                try
                {
                    if (process.Process.WaitForExit(waitTimeMs))
                    {
                        return;
                    }
                }
                catch (Exception ex)
                {
                    _logger.Error("Error in WaitForExit", ex);
                }

                if (enableForceKill)
                {
                    process.Process.Kill();
                }
            }
            catch (Exception ex)
            {
                _logger.ErrorException("Error killing process", ex);
            }
        }

        private void StopProcesses()
        {
            List<ProcessWrapper> proceses;
            lock (_runningProcesses)
            {
                proceses = _runningProcesses.ToList();
            }
            _runningProcesses.Clear();

            foreach (var process in proceses)
            {
                if (!process.HasExited)
                {
                    StopProcess(process, 500, true);
                }
            }
        }

        public string EscapeSubtitleFilterPath(string path)
        {
            return path.Replace('\\', '/').Replace(":/", "\\:/").Replace("'", "'\\\\\\''");
        }

        /// <summary>
        /// Performs application-defined tasks associated with freeing, releasing, or resetting unmanaged resources.
        /// </summary>
        public void Dispose()
        {
            Dispose(true);
        }

        /// <summary>
        /// Releases unmanaged and - optionally - managed resources.
        /// </summary>
        /// <param name="dispose"><c>true</c> to release both managed and unmanaged resources; <c>false</c> to release only unmanaged resources.</param>
        protected virtual void Dispose(bool dispose)
        {
            if (dispose)
            {
                _videoImageResourcePool.Dispose();
                StopProcesses();
            }
        }

        private class ProcessWrapper : IDisposable
        {
            public readonly Process Process;
            public bool HasExited;
            public int? ExitCode;
            private readonly MediaEncoder _mediaEncoder;
            private readonly ILogger _logger;

            public ProcessWrapper(Process process, MediaEncoder mediaEncoder, ILogger logger)
            {
                Process = process;
                _mediaEncoder = mediaEncoder;
                _logger = logger;
                Process.Exited += Process_Exited;
            }

            void Process_Exited(object sender, EventArgs e)
            {
                var process = (Process)sender;

                HasExited = true;

                try
                {
                    ExitCode = process.ExitCode;
                }
                catch (Exception ex)
                {
                }

                lock (_mediaEncoder._runningProcesses)
                {
                    _mediaEncoder._runningProcesses.Remove(this);
                }

                try
                {
                    process.Dispose();
                }
                catch (Exception ex)
                {
                }
            }

            private bool _disposed;
            private readonly object _syncLock = new object();
            public void Dispose()
            {
                lock (_syncLock)
                {
                    if (!_disposed)
                    {
                        if (Process != null)
                        {
                            Process.Exited -= Process_Exited;
                            Process.Dispose();
                        }
                    }

                    _disposed = true;
                }
            }
        }
    }
}