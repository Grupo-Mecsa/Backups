namespace Backup.Domain.Jobs;

/// <summary>
/// Política de retención aplicada en el destino tras cada respaldo exitoso.
/// Un respaldo se conserva si cumple cualquiera de los criterios activos.
/// </summary>
public sealed class RetentionPolicy
{
    /// <summary>Cantidad mínima de respaldos recientes a conservar. 0 = sin límite por cantidad.</summary>
    public int KeepLast { get; set; } = 7;

    /// <summary>Respaldos más nuevos que esta cantidad de días se conservan. 0 = sin límite por antigüedad.</summary>
    public int KeepDays { get; set; }

    public bool IsUnlimited => KeepLast <= 0 && KeepDays <= 0;
}
