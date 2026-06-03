namespace Stampd.Crypto.Vault;

/// <summary>
/// Vault authentication method. Production deployments should prefer AppRole or Kubernetes
/// over raw token auth — both rotate transparently and avoid baking a long-lived token into
/// configuration.
/// </summary>
public enum VaultAuthMethod
{
    /// <summary>Direct token (legacy / dev). Long-lived token in config; no rotation.</summary>
    Token = 0,

    /// <summary>
    /// AppRole (role-id + secret-id). Standard production auth for non-K8s workloads —
    /// CI runners, VMs, on-prem services.
    /// </summary>
    AppRole = 1,

    /// <summary>
    /// Kubernetes ServiceAccount JWT. Standard production auth for workloads running inside
    /// a Kubernetes cluster; reads the projected SA token from the pod filesystem.
    /// </summary>
    Kubernetes = 2,
}

/// <summary>
/// Configuration for <see cref="VaultSealingProvider"/>.
/// </summary>
public sealed class VaultSealingOptions
{
    /// <summary>
    /// Vault HTTP endpoint, e.g. <c>https://vault.example.com:8200</c>. Works for both
    /// HashiCorp Vault and OpenBao.
    /// </summary>
    public Uri? VaultAddress { get; set; }

    /// <summary>
    /// Selected authentication method. Defaults to <see cref="VaultAuthMethod.Token"/> for
    /// backward compatibility with the dev-mode setup; production should switch to
    /// <see cref="VaultAuthMethod.AppRole"/> or <see cref="VaultAuthMethod.Kubernetes"/>.
    /// </summary>
    public VaultAuthMethod AuthMethod { get; set; } = VaultAuthMethod.Token;

    /// <summary>
    /// Vault auth token. Required when <see cref="AuthMethod"/> = <see cref="VaultAuthMethod.Token"/>.
    /// For production use, prefer rotating tokens via AppRole or Kubernetes.
    /// </summary>
    public string? Token { get; set; }

    /// <summary>AppRole role-id. Required when <see cref="AuthMethod"/> = <see cref="VaultAuthMethod.AppRole"/>.</summary>
    public string? AppRoleId { get; set; }

    /// <summary>AppRole secret-id. Required when <see cref="AuthMethod"/> = <see cref="VaultAuthMethod.AppRole"/>.</summary>
    public string? AppRoleSecretId { get; set; }

    /// <summary>
    /// Mount path of the AppRole auth method. Defaults to "approle". Vault admins who
    /// mounted AppRole at a non-default path should set this explicitly.
    /// </summary>
    public string AppRoleMountPath { get; set; } = "approle";

    /// <summary>
    /// Kubernetes Vault role name. Required when <see cref="AuthMethod"/> = <see cref="VaultAuthMethod.Kubernetes"/>.
    /// Maps to the <c>role</c> body parameter in POST <c>/v1/auth/kubernetes/login</c>.
    /// </summary>
    public string? KubernetesRole { get; set; }

    /// <summary>
    /// Filesystem path to the projected ServiceAccount JWT. Defaults to the standard
    /// kubelet projection path. Override when running with non-default projection (e.g.
    /// bound SA tokens with a custom <c>audience</c> claim).
    /// </summary>
    public string KubernetesServiceAccountTokenPath { get; set; }
        = "/var/run/secrets/kubernetes.io/serviceaccount/token";

    /// <summary>
    /// Mount path of the Kubernetes auth method. Defaults to "kubernetes".
    /// </summary>
    public string KubernetesMountPath { get; set; } = "kubernetes";

    /// <summary>
    /// Mount path of the Transit secrets engine. Defaults to "transit".
    /// </summary>
    public string TransitMountPath { get; set; } = "transit";

    /// <summary>Name of the Transit key used for signing.</summary>
    public string? KeyName { get; set; }

    /// <summary>
    /// Local filesystem path to the X.509 certificate (.cer / .crt / .pem) whose public
    /// key matches the Transit key. Vault doesn't store the X.509 wrapper alongside the
    /// key — adopters provide it separately.
    /// </summary>
    public string? CertificatePath { get; set; }

    public void Validate()
    {
        if (VaultAddress is null)
        {
            throw new InvalidOperationException(
                $"{nameof(VaultSealingOptions)}.{nameof(VaultAddress)} is required.");
        }

        ValidateAuthMethod();

        if (string.IsNullOrWhiteSpace(KeyName))
        {
            throw new InvalidOperationException(
                $"{nameof(VaultSealingOptions)}.{nameof(KeyName)} is required.");
        }

        if (string.IsNullOrWhiteSpace(CertificatePath))
        {
            throw new InvalidOperationException(
                $"{nameof(VaultSealingOptions)}.{nameof(CertificatePath)} is required " +
                "(the X.509 cert whose public key corresponds to the Transit key).");
        }
    }

    private void ValidateAuthMethod()
    {
        switch (AuthMethod)
        {
            case VaultAuthMethod.Token:
                if (string.IsNullOrWhiteSpace(Token))
                {
                    throw new InvalidOperationException(
                        $"{nameof(VaultSealingOptions)}.{nameof(Token)} is required when " +
                        $"{nameof(AuthMethod)} = {nameof(VaultAuthMethod.Token)}.");
                }
                break;

            case VaultAuthMethod.AppRole:
                if (string.IsNullOrWhiteSpace(AppRoleId))
                {
                    throw new InvalidOperationException(
                        $"{nameof(VaultSealingOptions)}.{nameof(AppRoleId)} is required when " +
                        $"{nameof(AuthMethod)} = {nameof(VaultAuthMethod.AppRole)}.");
                }

                if (string.IsNullOrWhiteSpace(AppRoleSecretId))
                {
                    throw new InvalidOperationException(
                        $"{nameof(VaultSealingOptions)}.{nameof(AppRoleSecretId)} is required when " +
                        $"{nameof(AuthMethod)} = {nameof(VaultAuthMethod.AppRole)}.");
                }
                break;

            case VaultAuthMethod.Kubernetes:
                if (string.IsNullOrWhiteSpace(KubernetesRole))
                {
                    throw new InvalidOperationException(
                        $"{nameof(VaultSealingOptions)}.{nameof(KubernetesRole)} is required when " +
                        $"{nameof(AuthMethod)} = {nameof(VaultAuthMethod.Kubernetes)}.");
                }

                if (!File.Exists(KubernetesServiceAccountTokenPath))
                {
                    throw new InvalidOperationException(
                        $"Kubernetes ServiceAccount token not found at " +
                        $"'{KubernetesServiceAccountTokenPath}'. Stampd must run inside a pod " +
                        $"with the projected SA token mounted, or you must override " +
                        $"{nameof(KubernetesServiceAccountTokenPath)}.");
                }
                break;

            default:
                throw new InvalidOperationException(
                    $"Unknown {nameof(VaultAuthMethod)} value: {AuthMethod}.");
        }
    }
}
