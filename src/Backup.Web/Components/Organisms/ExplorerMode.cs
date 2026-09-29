namespace Backup.Web.Components.Organisms;

public enum ExplorerMode
{
    /// <summary>Navegar y elegir una carpeta.</summary>
    PickFolder,

    /// <summary>Navegar y elegir una carpeta o un archivo.</summary>
    PickFolderOrFile,

    /// <summary>Marcar con casillas qué se incluye y qué se excluye bajo una raíz.</summary>
    SelectItems,
}
