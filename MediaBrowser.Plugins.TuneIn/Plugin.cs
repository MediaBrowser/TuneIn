using System;
using System.Collections.Generic;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using MediaBrowser.Plugins.TuneIn.Configuration;
using System.IO;
using MediaBrowser.Model.Drawing;
using MediaBrowser.Controller;

namespace MediaBrowser.Plugins.TuneIn
{
    /// <summary>
    /// Class Plugin
    /// </summary>
    public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages, IHasThumbImage
    {
        
        public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer, IServerApplicationHost applicationHost)
            : base(applicationPaths, xmlSerializer)
        {
            if (!SupportsServerVersion(applicationHost.ApplicationVersion))
                throw new NotSupportedException("TuneIn requires patched Emby Server 4.9.1.81+ or beta 4.9.2.7+.");
            Instance = this;
        }

        internal static bool SupportsServerVersion(Version version) =>
            version != null && version >= new Version(4, 9, 1, 81)
                && (version < new Version(4, 9, 2, 0) || version >= new Version(4, 9, 2, 7));

        public IEnumerable<PluginPageInfo> GetPages()
        {
            return new[]
            {
                new PluginPageInfo
                {
                    Name = "tunein",
                    EmbeddedResourcePath = GetType().Namespace + ".Configuration.configPage.html"
                },
                new PluginPageInfo
                {
                    Name = "tuneinjs",
                    EmbeddedResourcePath = GetType().Namespace + ".Configuration.configPage.js"
                }
            };
        }

        private Guid _id = new Guid("10b82431-f939-4507-a8e2-9509dd3fd6ae");
        public override Guid Id
        {
            get { return _id; }
        }

        /// <summary>
        /// Gets the name of the plugin
        /// </summary>
        /// <value>The name.</value>
        public override string Name
        {
            get { return "TuneIn"; }
        }

        /// <summary>
        /// Gets the description.
        /// </summary>
        /// <value>The description.</value>
        public override string Description
        {
            get
            {
                return "TuneIn streams in your collection.";
            }
        }

        public Stream GetThumbImage()
        {
            var type = GetType();
            return type.Assembly.GetManifestResourceStream(type.Namespace + ".Images.thumb.png");
        }

        public ImageFormat ThumbImageFormat
        {
            get
            {
                return ImageFormat.Png;
            }
        }

        /// <summary>
        /// Gets the instance.
        /// </summary>
        /// <value>The instance.</value>
        public static Plugin Instance { get; private set; }
    }
}
