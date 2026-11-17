using Backup.Application.Providers;

namespace Backup.Providers.Cloud.S3;

/// <summary>
/// Servicios compatibles con S3: la implementación es la de <see cref="S3Source"/> y <see cref="S3Destination"/>, y
/// cada servicio solo pide lo que lo identifica (ID de cuenta, región...) y arma el endpoint por su cuenta.
/// </summary>
public static class S3Variants
{
    public static readonly ProviderVariant CloudflareR2 = Create(
        "r2", "Cloudflare R2", "Buckets de Cloudflare R2, sin costos de salida.",
        extra:
        [
            SettingField.Text("accountId", "ID de cuenta", required: true, placeholder: "0123456789abcdef0123456789abcdef",
                help: "Panel de Cloudflare → R2 → «Account ID».").ForConnection(),
            SettingField.Select("jurisdiction", "Jurisdicción", ["default", "eu", "fedramp"], "default",
                "Solo si el bucket se creó con jurisdicción específica (UE o FedRAMP).").ForConnection(),
        ],
        hidden: ["region", "serviceUrl", "forcePathStyle"],
        accessKeyHelp: "R2 → Administrar tokens de API → Crear token (Access Key ID y Secret Access Key).",
        storageClasses: ["STANDARD", "STANDARD_IA"],
        normalize: s =>
        {
            var jurisdiction = s.Get("jurisdiction", "default");
            var host = jurisdiction == "default" ? "r2.cloudflarestorage.com" : $"{jurisdiction}.r2.cloudflarestorage.com";
            return s.With(("serviceUrl", $"https://{s.Require("accountId")}.{host}"), ("region", "auto"), ("forcePathStyle", "true"));
        });

    public static readonly ProviderVariant BackblazeB2 = Create(
        "b2", "Backblaze B2", "Buckets de Backblaze B2 por su API compatible con S3.",
        hidden: ["serviceUrl", "forcePathStyle", "storageClass"],
        region: f => f with
        {
            Required = true,
            DefaultValue = null,
            Placeholder = "us-west-004",
            Help = "Aparece en el endpoint del bucket: s3.<región>.backblazeb2.com.",
        },
        accessKeyHelp: "Application Keys → Add a New Application Key (keyID y applicationKey).",
        normalize: s => s.With(("serviceUrl", $"https://s3.{s.Require("region")}.backblazeb2.com")));

    public static readonly ProviderVariant Wasabi = Create(
        "wasabi", "Wasabi", "Almacenamiento en caliente de Wasabi, compatible con S3.",
        hidden: ["serviceUrl", "forcePathStyle", "storageClass"],
        region: f => f with { Help = "Región del bucket: us-east-1, us-east-2, us-west-1, eu-central-1, ap-northeast-1..." },
        accessKeyHelp: "Consola de Wasabi → Access Keys → Create New Access Key.",
        normalize: s => s.With(("serviceUrl", $"https://s3.{s.Get("region", "us-east-1")}.wasabisys.com")));

    public static readonly ProviderVariant GoogleCloudStorage = Create(
        "gcs", "Google Cloud Storage", "Buckets de Google Cloud Storage con claves HMAC (API de interoperabilidad).",
        hidden: ["region", "serviceUrl", "forcePathStyle"],
        accessKeyHelp: "Cloud Storage → Configuración → Interoperabilidad → Crear una clave para una cuenta de servicio (HMAC).",
        storageClasses: ["STANDARD", "NEARLINE", "COLDLINE", "ARCHIVE"],
        normalize: s => s.With(("serviceUrl", "https://storage.googleapis.com"), ("region", "auto")));

    public static readonly ProviderVariant DigitalOceanSpaces = Create(
        "spaces", "DigitalOcean Spaces", "Spaces de DigitalOcean, compatible con S3.",
        hidden: ["serviceUrl", "forcePathStyle", "storageClass"],
        region: f => SettingField.Select("region", "Región", ["nyc3", "sfo2", "sfo3", "ams3", "fra1", "lon1", "sgp1", "syd1", "blr1", "tor1", "atl1"], "nyc3")
            .ForConnection(),
        accessKeyHelp: "Panel de DigitalOcean → API → Spaces Keys → Generate New Key.",
        // Spaces firma con "us-east-1"; la región real va en el endpoint.
        normalize: s => s.With(("serviceUrl", $"https://{s.Get("region", "nyc3")}.digitaloceanspaces.com"), ("region", "us-east-1")));

    public static readonly ProviderVariant SupabaseStorage = Create(
        "supabase-storage", "Supabase Storage", "Buckets de Supabase Storage por su API compatible con S3.",
        extra:
        [
            SettingField.Text("projectRef", "ID del proyecto", required: true, placeholder: "abcdefghijklmnop",
                help: "Lo que va antes de .supabase.co en la URL del proyecto.").ForConnection(),
        ],
        hidden: ["serviceUrl", "forcePathStyle", "storageClass"],
        region: f => f with
        {
            Required = true,
            DefaultValue = null,
            Placeholder = "us-east-1",
            Help = "Región del proyecto: Storage → Settings → S3 Connection.",
        },
        accessKeyHelp: "Storage → Settings → S3 Connection → New access key. Da acceso total a Storage: guárdala con cuidado.",
        normalize: s => s.With(
            ("serviceUrl", $"https://{s.Require("projectRef")}.storage.supabase.co/storage/v1/s3"),
            ("forcePathStyle", "true")));

    public static readonly ProviderVariant MinIO = Create(
        "minio", "MinIO", "Servidor MinIO propio (o cualquier S3 autoalojado con rutas path-style).",
        hidden: ["forcePathStyle", "storageClass"],
        accessKeyHelp: "Consola de MinIO → Access Keys → Create access key.",
        customize: f => f.Key == "serviceUrl"
            ? f with { Label = "URL del servidor", Required = true, Placeholder = "https://minio.empresa.local:9000", Help = null }
            : f,
        normalize: s => s.With(("forcePathStyle", "true")));

    public static IReadOnlyList<ProviderVariant> All { get; } =
        [CloudflareR2, BackblazeB2, Wasabi, GoogleCloudStorage, DigitalOceanSpaces, SupabaseStorage, MinIO];

    /// <param name="hidden">Campos del S3 genérico que el servicio fija por su cuenta.</param>
    /// <param name="storageClasses">Clases que acepta el servicio; null = el campo se quita si no está en <paramref name="hidden"/>.</param>
    private static ProviderVariant Create(
        string key,
        string name,
        string description,
        Func<ProviderSettings, ProviderSettings> normalize,
        string accessKeyHelp,
        IReadOnlyList<SettingField>? extra = null,
        IReadOnlyList<string>? hidden = null,
        IReadOnlyList<string>? storageClasses = null,
        Func<SettingField, SettingField>? region = null,
        Func<SettingField, SettingField>? customize = null) => new(key, name, description)
    {
        Icon = "cloud",
        ExtraFields = extra ?? [],
        Normalize = normalize,
        Customize = field =>
        {
            if (hidden?.Contains(field.Key, StringComparer.OrdinalIgnoreCase) == true)
            {
                return null;
            }

            SettingField? result = field.Key switch
            {
                "accessKey" => field with { Required = true, Help = accessKeyHelp },
                "secretKey" => field with { Required = true },
                "region" when region is not null => region(field),
                "storageClass" => storageClasses is null
                    ? null
                    : field with { Options = storageClasses, DefaultValue = storageClasses[0], Help = null },
                _ => field,
            };

            return result is null ? null : customize?.Invoke(result) ?? result;
        },
    };
}
