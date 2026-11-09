#nullable disable

#pragma warning disable CS1591

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Serialization;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.PirateFin;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;

namespace MediaBrowser.Controller.Entities.Movies
{
    /// <summary>
    /// Class Movie.
    /// </summary>
    public class Movie : Video, IHasSpecialFeatures, IHasTrailers, IHasLookupInfo<MovieInfo>, ISupportsBoxSetGrouping, IPirateFinDownloadable
    {
        /// <summary>
        /// Gets or sets the magnet url of the release (PirateFin).
        /// </summary>
        public string DownloadUrl { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the seeders of the release (PirateFin).
        /// </summary>
        public int? Seeders { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether <see cref="DownloadUrl"/> is set (PirateFin).
        /// </summary>
        public bool IsDownloadable { get; set; }

        /// <summary>
        /// Gets or sets when the download started, in UTC; <c>null</c> when not downloading (PirateFin).
        /// </summary>
        public DateTime? DownloadStartedUtc { get; set; }

        public override bool CanDelete()
        {
            // PirateFin items have no file on disk until downloaded; library scans must never remove them (even once the source search gave up and Scheduled = false).
            if (IsInternet && !IsDownloaded && !string.IsNullOrEmpty(Path) && !File.Exists(Path))
            {
                return false;
            }

            if (IsActiveRecording())
            {
                return false;
            }

            return base.CanDelete();
        }

        /// <inheritdoc />
        [JsonIgnore]
        public IReadOnlyList<Guid> SpecialFeatureIds => GetExtras()
            .Where(extra => extra.ExtraType is not null && extra is Video)
            .Select(extra => extra.Id)
            .ToArray();

        /// <inheritdoc />
        [JsonIgnore]
        public IReadOnlyList<BaseItem> LocalTrailers => GetExtras([Model.Entities.ExtraType.Trailer]).ToArray();

        /// <summary>
        /// Gets or sets the name of the TMDb collection.
        /// </summary>
        /// <value>The name of the TMDb collection.</value>
        public string TmdbCollectionName { get; set; }

        [JsonIgnore]
        public string CollectionName
        {
            get => TmdbCollectionName;
            set => TmdbCollectionName = value;
        }

        public override double GetDefaultPrimaryImageAspectRatio()
        {
            // hack for tv plugins
            if (SourceType == SourceType.Channel)
            {
                return 0;
            }

            return 2.0 / 3;
        }

        /// <inheritdoc />
        public override UnratedItem GetBlockUnratedType()
        {
            return UnratedItem.Movie;
        }

        public MovieInfo GetLookupInfo()
        {
            var info = GetItemLookupInfo<MovieInfo>();

            if (!IsInMixedFolder)
            {
                var name = System.IO.Path.GetFileName(ContainingFolderPath);

                if (VideoType == VideoType.VideoFile || VideoType == VideoType.Iso)
                {
                    if (string.Equals(name, System.IO.Path.GetFileName(Path), StringComparison.OrdinalIgnoreCase))
                    {
                        // if the folder has the file extension, strip it
                        name = System.IO.Path.GetFileNameWithoutExtension(name);
                    }
                }

                info.Name = name;
            }

            return info;
        }

        /// <inheritdoc />
        public override bool BeforeMetadataRefresh(bool replaceAllMetadata)
        {
            var hasChanges = base.BeforeMetadataRefresh(replaceAllMetadata);

            if (!ProductionYear.HasValue)
            {
                var info = LibraryManager.ParseName(Name);

                var yearInName = info.Year;

                if (yearInName.HasValue)
                {
                    ProductionYear = yearInName;
                    hasChanges = true;
                }
                else
                {
                    // Try to get the year from the folder name
                    if (!IsInMixedFolder)
                    {
                        info = LibraryManager.ParseName(System.IO.Path.GetFileName(ContainingFolderPath));

                        yearInName = info.Year;

                        if (yearInName.HasValue)
                        {
                            ProductionYear = yearInName;
                            hasChanges = true;
                        }
                    }
                }
            }

            return hasChanges;
        }
    }
}
