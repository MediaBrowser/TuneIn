using MediaBrowser.Controller.Net;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Services;
using System;
using System.Net;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace MediaBrowser.Plugins.TuneIn
{
    [Route("/TuneIn/Relay/{Id}", "GET")]
    [Authenticated(AllowLocalOnly = true)]
    public class GetTuneInRelay
    {
        public string Id { get; set; }
    }

    public class TuneInService : IService, IRequiresRequest
    {
        private readonly IHttpResultFactory resultFactory;
        private readonly ILogger logger;
        private readonly TuneInRelay relay;
        private readonly IFfmpegManager ffmpegManager;

        public TuneInService(IHttpResultFactory resultFactory, ILogManager logManager, IFfmpegManager ffmpegManager)
            : this(resultFactory, logManager, new TuneInRelay(), ffmpegManager) { }

        internal TuneInService(IHttpResultFactory resultFactory, ILogManager logManager, TuneInRelay relay,
            IFfmpegManager ffmpegManager = null)
        {
            this.resultFactory = resultFactory ?? throw new ArgumentNullException(nameof(resultFactory));
            logger = logManager.GetLogger(GetType().Name);
            this.relay = relay;
            this.ffmpegManager = ffmpegManager;
        }

        public IRequest Request { get; set; }

        public async Task<object> Get(GetTuneInRelay request)
        {
            var remote = Request.RemoteIp;
            if (remote?.IsIPv4MappedToIPv6 == true) remote = remote.MapToIPv4();
            if (!Request.IsSocketConnectionLocal || remote == null || !IPAddress.IsLoopback(remote))
                throw new UnauthorizedAccessException("TuneIn relay access is restricted to this Emby server.");
            var response = await relay.OpenAsync(request.Id, Request.CancellationToken, message => logger.Warn(message),
                DecodeAudio).ConfigureAwait(false);
            try
            {
                Request.CancellationToken.ThrowIfCancellationRequested();
                return resultFactory.GetResult(Request, response.Body, response.ContentType, null);
            }
            catch
            {
                response.Dispose();
                throw;
            }
        }

        private Stream DecodeAudio(Security.SafeResponse source, CancellationToken token)
        {
            var configuration = ffmpegManager?.FfmpegConfiguration;
            return new PcmDecoderStream(configuration?.EncoderPath, source, token,
                configuration?.EnvironmentVariables?.ToDictionary(setting => setting.VariableName, setting => setting.Value));
        }
    }
}
