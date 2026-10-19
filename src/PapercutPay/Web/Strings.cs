using System.Globalization;

namespace PapercutPay.Web;

/// <summary>User-facing texts in French and English. The language is chosen once at startup (App:Language).</summary>
public static class Strings
{
    private static readonly Dictionary<string, (string Fr, string En)> Table = new()
    {
        ["top_up_title"] = ("Recharger mon crédit d'impression", "Top up my print credit"),
        ["page_title"] = ("Recharger mon crédit", "Top up my credit"),
        ["account"] = ("Compte d'impression", "Print account"),
        ["balance"] = ("Solde actuel", "Current balance"),
        ["amount"] = ("Montant à ajouter", "Amount to add"),
        ["pay"] = ("Payer par carte", "Pay by card"),
        ["secure_note"] = (
            "Vous serez redirigé vers la page de paiement sécurisée de Worldline. Vos données de carte ne sont jamais enregistrées par ce service.",
            "You will be redirected to Worldline's secure payment page. Your card details are never stored by this service."),
        ["footer"] = ("Paiement sécurisé par Worldline", "Secure payment by Worldline"),
        ["subtitle"] = ("Impression", "Printing"),
        ["reference"] = ("Référence", "Reference"),
        ["continue"] = ("Continuer", "Continue"),
        ["result_page_title"] = ("Résultat du paiement", "Payment result"),

        ["credited_title"] = ("Paiement accepté", "Payment accepted"),
        ["credited_text"] = ("Votre crédit d'impression a été mis à jour.", "Your print credit has been updated."),
        ["already_title"] = ("Paiement déjà pris en compte", "Payment already processed"),
        ["already_text"] = ("Votre crédit d'impression est à jour.", "Your print credit is up to date."),
        ["pending_title"] = ("Paiement en cours de traitement", "Payment being processed"),
        ["pending_text"] = (
            "Cette page se met à jour automatiquement. Ne fermez pas la fenêtre.",
            "This page refreshes automatically. Please do not close the window."),
        ["retry_title"] = ("Paiement accepté, crédit en attente", "Payment accepted, credit pending"),
        ["retry_text"] = (
            "La mise à jour de votre crédit n'a pas pu être faite pour le moment. Elle sera retentée automatiquement ; si rien ne change dans quelques minutes, contactez le support avec la référence ci-dessous.",
            "Your credit could not be updated right now. It will be retried automatically; if nothing changes within a few minutes, contact support with the reference below."),
        ["failed_title"] = ("Paiement annulé ou refusé", "Payment cancelled or declined"),
        ["failed_text"] = ("Aucun montant n'a été débité. Vous pouvez recommencer.", "You have not been charged. You can try again."),
        ["review_title"] = ("Vérification en cours", "Under review"),
        ["review_text"] = (
            "Votre paiement est bien reçu mais nécessite une vérification manuelle. Contactez le support avec la référence ci-dessous.",
            "Your payment was received but needs a manual check. Contact support with the reference below."),
        ["notfound_title"] = ("Commande introuvable", "Order not found"),
        ["notfound_text"] = ("Cette référence n'existe pas.", "This reference does not exist."),

        ["error_title"] = ("Recharge impossible", "Top-up unavailable"),
        ["err_invalid_link"] = (
            "Lien de recharge invalide ou expiré. Relancez la recharge depuis PaperCut.",
            "Invalid or expired top-up link. Please start again from PaperCut."),
        ["err_account"] = ("Compte PaperCut introuvable.", "PaperCut account not found."),
        ["err_papercut"] = (
            "Le serveur d'impression est momentanément indisponible. Réessayez plus tard.",
            "The print server is temporarily unavailable. Please try again later."),
        ["err_request"] = ("Demande invalide.", "Invalid request."),
        ["err_payment"] = (
            "Le service de paiement est momentanément indisponible. Réessayez plus tard.",
            "The payment service is temporarily unavailable. Please try again later."),
    };

    /// <summary>"fr" or "en". Anything else falls back to English.</summary>
    public static string Language { get; private set; } = "en";

    public static bool IsFrench => Language == "fr";

    public static CultureInfo Culture => CultureInfo.GetCultureInfo(IsFrench ? "fr-FR" : "en-GB");

    public static void Configure(string? language) =>
        Language = string.Equals(language, "fr", StringComparison.OrdinalIgnoreCase) ? "fr" : "en";

    public static string T(string key) => Table.TryGetValue(key, out var v) ? (IsFrench ? v.Fr : v.En) : key;

    internal static IEnumerable<string> Keys => Table.Keys;

    internal static (string Fr, string En) Both(string key) => Table[key];
}
