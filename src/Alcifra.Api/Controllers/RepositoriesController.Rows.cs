using Alcifra.Application.Abstractions;

namespace Alcifra.Api.Controllers;

/// <summary>
/// The repository listings' ROW TYPES, split from <see cref="RepositoriesController"/> when the file met the
/// 1000-line ceiling (CLAUDE.md): what the repositories listing and its documents listing answer per row — their
/// hypermedia and their capability flags (<see cref="Hypermedia.ICarriesRowCapabilities"/>) — beside the endpoints
/// that stamp them.
/// </summary>
public partial class RepositoriesController
{
    public class RepositoryResource : HypermediaResource, Hypermedia.ICarriesRowCapabilities
    {
        /// <summary>May the caller DELETE this repository? (#858)</summary>
        /// <remarks>
        /// A tree ROOT is where the destructive gap was most visible: both clients offered Delete, Rename and
        /// Move on a repository's context menu with no gate at all. Flags rather than rels for ADR 0719's
        /// reason — DELETE and PUT are at the item's own address.
        /// </remarks>
        public bool CanDelete { get; set; }

        /// <summary>May the caller rename this repository or change its index data / contents order? (#858)</summary>
        public bool CanEditIndexData { get; set; }

        /// <summary>May this repository be moved (i.e. DEMOTED under a folder)? (#858)</summary>
        /// <remarks>
        /// For a root this is the demotion case, which needs the CanManageRepositories system right on top of
        /// CanMove — so it is emphatically not "has a parent". The row reports CanMove alone; the system right
        /// is already in the client's hands from whoami, and the server enforces both.
        /// </remarks>
        public bool CanMove { get; set; }

        /// <summary>May the caller manage this row's permissions? (#858)</summary>
        public bool CanManagePermissions { get; set; }

        /// <summary>May the caller file a NEW VERSION of this row — the content right the versions POST enforces?</summary>
        public bool CanEditContent { get; set; }

        /// <summary>May the caller create a plain child inside this row? (#854)</summary>
        /// <remarks>Policy AND right — see <c>ICarriesRowCapabilities.CanCreateChildren</c>.</remarks>
        public bool CanCreateChildren { get; set; }


        public Guid Id { get; set; }

        public string Name { get; set; } = string.Empty;

        // Presentation metadata for the browse tree (ADR "Blazor repository/document browsing"):
        // HasChildren governs whether the contents list can drill in; HasVersions picks the icon;
        // HasSubfolders (a child that is itself a folder — no versions) governs the folder tree's expand
        // caret, since the tree shows only folders (ADR "Workbench pane content fixes"). Computed, never
        // stored.
        public bool HasChildren { get; set; }

        public bool HasVersions { get; set; }

        // A repository is always a container (#1708): every row says isFolder from the server, never from "has no file".
        public bool IsFolder { get; set; } = true;

        public bool HasSubfolders { get; set; }

        /// <summary>What a client may create in this repository, with the address for each (#673).</summary>
        /// <remarks>
        /// On the ROW for the same reason `create-child` is, and stated one paragraph below: a tree's top-level
        /// nodes get their whole menu from this listing and nothing re-fetches a node to fill one in. Carried
        /// here and on the children listing — the two places a folder is ever LISTED — so every folder in the
        /// tree answers the question, not just the ones somebody drilled into.
        /// </remarks>
        public List<Documents.CreatableChild> Admits { get; set; } = [];

        /// <summary>What to draw for this repository — a token from its mask (see the children listing).</summary>
        public string? Icon { get; set; }
    }

    private record RepositoryRow(Guid Id, string Name, DateTimeOffset CreatedAt, bool HasChildren, bool HasVersions, bool HasSubfolders, Guid? MaskId);

    public class RepositoryListResource : HypermediaResource
    {
        public List<RepositoryResource> Repositories { get; set; } = [];
    }
}
