using System;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Server.Implementations.Item;
using MediaBrowser.Controller;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Server.Implementations.Tests.Item;

public class BaseItemRepositoryTests
{
    [Fact]
    public void DeserializeBaseItem_WithUnknownType_ReturnsNull()
    {
        // Arrange
        var entity = new BaseItemEntity
        {
            Id = Guid.NewGuid(),
            Type = "NonExistent.Plugin.CustomItemType"
        };

        // Act
        var result = BaseItemRepository.DeserializeBaseItem(entity, NullLogger.Instance, null, false);

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public void DeserializeBaseItem_WithUnknownType_LogsWarning()
    {
        // Arrange
        var entity = new BaseItemEntity
        {
            Id = Guid.NewGuid(),
            Type = "NonExistent.Plugin.CustomItemType"
        };
        var loggerMock = new Mock<ILogger>();

        // Act
        BaseItemRepository.DeserializeBaseItem(entity, loggerMock.Object, null, false);

        // Assert
        loggerMock.Verify(
            x => x.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => v.ToString()!.Contains("unknown type", StringComparison.OrdinalIgnoreCase)),
                It.IsAny<Exception?>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    [Fact]
    public void DeserializeBaseItem_WithKnownType_ReturnsItem()
    {
        // Arrange
        var entity = new BaseItemEntity
        {
            Id = Guid.NewGuid(),
            Type = "MediaBrowser.Controller.Entities.Movies.Movie"
        };

        // Act
        var result = BaseItemRepository.DeserializeBaseItem(entity, NullLogger.Instance, null, false);

        // Assert
        Assert.NotNull(result);
    }

    [Fact]
    public void MapEpisode_PreservesSchedulePropertiesAndData()
    {
        var appHostMock = new Mock<IServerApplicationHost>();
        var episode = new MediaBrowser.Controller.Entities.TV.Episode
        {
            Id = Guid.NewGuid(),
            Name = "Test Episode",
            IsInternet = true,
            Scheduled = true,
            IsDownloaded = true,
            DownloadUrl = "http://example.com/test.mp4"
        };

        var entity = BaseItemMapper.Map(episode, appHostMock.Object);

        Assert.True(entity.IsInternet);
        Assert.True(entity.Scheduled);
        Assert.True(entity.IsDownloaded);
        Assert.NotNull(entity.Data);
        Assert.Contains("DownloadUrl", entity.Data, StringComparison.Ordinal);
        Assert.Contains("Scheduled", entity.Data, StringComparison.Ordinal);
        Assert.Contains("IsInternet", entity.Data, StringComparison.Ordinal);
        Assert.Contains("IsDownloaded", entity.Data, StringComparison.Ordinal);

        var restored = BaseItemRepository.DeserializeBaseItem(entity, NullLogger.Instance, appHostMock.Object, false) as MediaBrowser.Controller.Entities.TV.Episode;

        Assert.NotNull(restored);
        Assert.True(restored.IsInternet);
        Assert.True(restored.Scheduled);
        Assert.True(restored.IsDownloaded);
        Assert.Equal("http://example.com/test.mp4", restored.DownloadUrl);
    }
}
