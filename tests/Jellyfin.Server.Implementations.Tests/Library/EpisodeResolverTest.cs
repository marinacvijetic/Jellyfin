using Emby.Naming.Common;
using Emby.Server.Implementations.Library.Resolvers.TV;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.Server.Implementations.Tests.Library
{
    public class EpisodeResolverTest
    {
        private static readonly NamingOptions _namingOptions = new();

        [Fact]
        public void Resolve_GivenVideoInExtrasFolder_DoesNotResolveToEpisode()
        {
            var parent = new Folder { Name = "extras" };

            var episodeResolver = new EpisodeResolver(Mock.Of<ILogger<EpisodeResolver>>(), _namingOptions);
            var itemResolveArgs = new ItemResolveArgs(
                Mock.Of<IServerApplicationPaths>(),
                Mock.Of<IDirectoryService>())
            {
                Parent = parent,
                CollectionType = CollectionType.TvShows,
                FileInfo = new FileSystemMetadata
                {
                    FullName = "All My Children/Season 01/Extras/All My Children S01E01 - Behind The Scenes.mkv"
                }
            };

            Assert.Null(episodeResolver.Resolve(itemResolveArgs));
        }

        [Fact]
        public void Resolve_GivenVideoInExtrasSeriesFolder_ResolvesToEpisode()
        {
            var series = new Series { Name = "Extras" };

            // Have to create a mock because of moq proxies not being castable to a concrete implementation
            // https://github.com/jellyfin/jellyfin/blob/ab0cff8556403e123642dc9717ba778329554634/Emby.Server.Implementations/Library/Resolvers/BaseVideoResolver.cs#L48
            var episodeResolver = new EpisodeResolverMock(Mock.Of<ILogger<EpisodeResolver>>(), _namingOptions);
            var itemResolveArgs = new ItemResolveArgs(
                Mock.Of<IServerApplicationPaths>(),
                Mock.Of<IDirectoryService>())
            {
                Parent = series,
                CollectionType = CollectionType.TvShows,
                FileInfo = new FileSystemMetadata
                {
                    FullName = "Extras/Extras S01E01.mkv"
                }
            };
            Assert.NotNull(episodeResolver.Resolve(itemResolveArgs));
        }

        private class EpisodeResolverMock : EpisodeResolver
        {
            public EpisodeResolverMock(ILogger<EpisodeResolver> logger, NamingOptions namingOptions) : base(logger, namingOptions)
            {
            }

            protected override TVideoType ResolveVideo<TVideoType>(ItemResolveArgs args, bool parseName) => new();
        }
    }
}
