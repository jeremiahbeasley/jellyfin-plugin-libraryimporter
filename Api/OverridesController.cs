using System.Net.Mime;
using Jellyfin.Plugin.LibraryImporter.Models;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.LibraryImporter.Api;

[ApiController]
[Route("LibraryImporter")]
[Authorize(Policy = "RequiresElevation")]
[Produces(MediaTypeNames.Application.Json)]
public class OverridesController : ControllerBase
{
    private readonly ILibraryManager _libraryManager;

    public OverridesController(ILibraryManager libraryManager)
    {
        _libraryManager = libraryManager;
    }

    [HttpGet("Overrides")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<List<OverrideEntry>> GetOverrides()
    {
        var config = LibraryImporterPlugin.Instance?.Configuration;
        return config?.Overrides ?? [];
    }

    [HttpPost("Overrides")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<OverrideEntry> AddOverride([FromBody] OverrideEntry entry)
    {
        var plugin = LibraryImporterPlugin.Instance;
        if (plugin is null) return StatusCode(500);

        entry.Id = Guid.NewGuid().ToString("N");
        plugin.Configuration.Overrides.Add(entry);
        plugin.SaveConfiguration();
        return entry;
    }

    [HttpPut("Overrides/{id}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<OverrideEntry> UpdateOverride(string id, [FromBody] OverrideEntry entry)
    {
        var plugin = LibraryImporterPlugin.Instance;
        if (plugin is null) return StatusCode(500);

        var idx = plugin.Configuration.Overrides.FindIndex(o => o.Id == id);
        if (idx < 0) return NotFound();

        entry.Id = id;
        plugin.Configuration.Overrides[idx] = entry;
        plugin.SaveConfiguration();
        return entry;
    }

    [HttpDelete("Overrides/{id}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult DeleteOverride(string id)
    {
        var plugin = LibraryImporterPlugin.Instance;
        if (plugin is null) return StatusCode(500);

        var removed = plugin.Configuration.Overrides.RemoveAll(o => o.Id == id);
        if (removed == 0) return NotFound();

        plugin.SaveConfiguration();
        return NoContent();
    }

    [HttpGet("Libraries")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<List<object>> GetLibraries()
    {
        var folders = _libraryManager.GetVirtualFolders();
        var result = folders.Select(f => new
        {
            f.Name,
            CollectionType = f.CollectionType?.ToString()?.ToLowerInvariant() ?? "unknown",
            Paths = f.Locations ?? [],
        }).ToList<object>();
        return result;
    }
}
