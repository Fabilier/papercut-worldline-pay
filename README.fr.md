# PapercutPay

> [English](README.md) | Français

Permet aux utilisateurs de recharger leur crédit d'impression **PaperCut MF** par carte bancaire, via **Worldline Direct Hosted Checkout**.

PapercutPay est un petit service ASP.NET Core 8 placé entre le portail utilisateur PaperCut et la page de paiement hébergée de Worldline. Il a été écrit pour remplacer les connecteurs historiques de type Ogone/Ingenico "Standard" (page de paiement hébergée), dont la disponibilité à long terme n'est pas garantie, en conservant le même parcours utilisateur.

> **État.** Éprouvé contre l'API **de pré-production** Worldline Direct (octobre 2026) et un vrai serveur PaperCut MF : paiement réussi, annulation, refus, rechargement de la page sans double crédit ; solde PaperCut lu et ajusté. La récupération automatique d'un retour navigateur perdu a été vérifiée avec la page de paiement simulée, pas encore avec un vrai paiement Worldline. Ce projet n'est ni affilié, ni approuvé, ni supporté par Worldline ou PaperCut. Ce logiciel manipule de l'argent : lisez les [notes de sécurité](SECURITY.md), testez en pré-production et relisez le code avant toute mise en production.

## Fonctionnement

```
 Portail PaperCut                      PapercutPay                          Worldline
        |  lien personnalisé ?user=%user%  |                                     |
        |--------------------------------->|  formulaire : choix du montant      |
        |                                  |-- CreateHostedCheckout ------------>|
        |<-------- redirection vers la page de paiement hébergée ----------------|
        |           (le client paie sur la page de Worldline)                    |
        |--------------------------------->|  GET /pay/return?o=<commande>       |
        |                                  |-- GetHostedCheckout --------------->|  statut lu de serveur à serveur
        |                                  |-- adjustUserAccountBalance -> PaperCut (une fois, après confirmation)
        |<---------- "Paiement accepté" ---|
```

Aucune donnée de carte n'atteint ce service : le client paie sur la page hébergée de Worldline.

## Garanties

- **Crédit seulement après confirmation.** Le compte PaperCut n'est crédité que si Worldline indique lui-même que le paiement est capturé, avec le même montant, la même devise et la même référence que la commande enregistrée. Rien dans l'URL de retour n'est cru.
- **Jamais deux fois.** Retour navigateur, contrôle automatique et webhook peuvent arriver en même temps : une prise atomique en base fait qu'un seul crédite.
- **Pannes sans risque.** Si PaperCut est injoignable, la commande reste rejouable. Si le résultat de l'appel PaperCut est inconnu (délai dépassé), la commande passe en `CREDIT_UNCERTAIN` et n'est jamais rejouée automatiquement.
- **Retours perdus récupérés.** Un contrôle automatique revérifie les commandes ouvertes chaque minute : un client qui ferme l'onglet après avoir payé est quand même crédité.
- **Montants côté serveur.** Le montant posté doit faire partie des montants configurés.
- **Pages durcies.** HTTPS imposé en mode Live, limitation de débit par adresse, en-têtes de sécurité stricts, aucun script ni ressource externe.

## Prérequis

- SDK .NET 8 pour compiler, ou runtime ASP.NET Core 8 pour exécuter (Hosting Bundle sous IIS).
- Un serveur PaperCut MF avec l'[API Web Services](https://www.papercut.com/help/manuals/ng-mf/common/tools-web-services/) activée : un jeton d'authentification et l'adresse de ce service dans les adresses autorisées.
- Un compte marchand Worldline Direct avec une clé et un secret d'API (Merchant Portal, Developer, Payment API).

## Démarrage rapide (sans PaperCut ni Worldline)

Le mode Fake utilise un PaperCut en mémoire avec un utilisateur (`demo`) et une page de paiement simulée :

```bash
dotnet run --project src/PapercutPay -- --App:Mode=Fake --App:LinkSecret=dev --App:DbPath=demo.db
```

Ouvrez l'adresse affichée dans la console ("Now listening on: ...") suivie de `/dev/link/demo`, choisissez un montant et utilisez la page de paiement simulée. Le mode Fake est refusé hors environnement `Development` sauf `App:AllowFakeMode=true`, car il expose des adresses qui fabriquent des liens de paiement.

Lancer les tests :

```bash
dotnet test
```

## Configuration

Les réglages sont lus, par priorité croissante, dans `appsettings.json`, un éventuel `appsettings.Local.json` à côté de l'application, `%ProgramData%\PapercutPay\appsettings.Production.json`, les variables d'environnement (`Worldline__ApiSecret`) puis la ligne de commande. Les secrets vont dans le fichier ProgramData ou les variables d'environnement, jamais dans le dépôt. Un modèle complet se trouve dans [`src/PapercutPay/appsettings.Production.example.json`](src/PapercutPay/appsettings.Production.example.json). La référence détaillée de chaque clé est dans le [README anglais](README.md#configuration).

Les clés indispensables en mode Live :

| Clé | Rôle |
| --- | --- |
| `App:PublicBaseUrl` | URL publique du service, chemin virtuel compris (URL de retour Worldline) |
| `App:LinkSecret` | Secret de signature des liens (obligatoire si `AllowUnsignedLinks` est faux) |
| `App:Language`, `App:Locale` | `fr` pour les pages du service, `fr_FR` pour la page Worldline |
| `App:BrandName`, `App:BrandColor` | Nom et couleur `#rrggbb` de l'en-tête |
| `PaperCut:BaseUrl`, `PaperCut:AuthToken` | API PaperCut (HTTP 9191 ou HTTPS 9192) |
| `Worldline:BaseUrl` | Pré-production : `https://payment.preprod.direct.worldline-solutions.com` ; production : `https://payment.direct.worldline-solutions.com` |
| `Worldline:MerchantId`, `ApiKeyId`, `ApiSecret` | Merchant Portal |

## Configurer PaperCut

1. Dans l'éditeur de configuration, mettez `auth.webservices.auth-token` à une valeur aléatoire longue et `auth.webservices.allowed-addresses` aux adresses de la machine qui exécute PapercutPay. Ne laissez pas l'accès ouvert à toutes les adresses.
2. Ajoutez un lien personnalisé au menu utilisateur web (Options, Général) vers le service :

   ```
   https://print.example.org/PapercutPay/pay?user=%user%
   ```

   PaperCut remplace `%user%` par le nom de l'utilisateur connecté.

## Configurer Worldline

- Créez une clé et un secret d'API dans le Merchant Portal. Le checkout hébergé est créé avec `authorizationMode=SALE`, car un compte peut être en autorisation seule par défaut (`PENDING_CAPTURE`), ce qui ne créditerait jamais personne.
- Statuts, cartes et cas de test : [documentation Worldline Direct](https://docs.direct.worldline-solutions.com/). En pré-production, un montant de 13,02 EUR est documenté comme déclenchant un refus.
- Le 3-D Secure se configure sur le compte Worldline, pas dans ce service. Vérifiez qu'il est actif avant la production.

## Exploitation

Commandes, à lancer depuis le dossier de l'application :

```bash
dotnet PapercutPay.dll orders 20                 # dernières commandes et leur état (sans secret)
dotnet PapercutPay.dll make-link <user> 24       # lien signé valable 24 heures
dotnet PapercutPay.dll pc-check <user>           # PaperCut : l'utilisateur existe ? solde ? (lecture seule)
dotnet PapercutPay.dll pc-adjust <user> 0.01     # PaperCut : ÉCRIT un ajustement, utiliser un compte de test
dotnet PapercutPay.dll probe                     # crée un checkout Worldline de 1,00 EUR et affiche l'échange
dotnet PapercutPay.dll probe-status <checkoutId> # relit un checkout
```

| État | Signification |
| --- | --- |
| `CREATED` | En attente de paiement, ou payée et en attente de crédit |
| `CREDITING` | En cours de crédit |
| `CREDITED` | Terminée |
| `FAILED` | Annulée, refusée ou expirée |
| `REVIEW` | Payée mais montant, devise ou référence différents : décision humaine |
| `CREDIT_UNCERTAIN` | Payée, mais l'appel PaperCut a un résultat inconnu : comparer le solde PaperCut et le paiement avant d'agir |

## Déploiement

Un script et un guide de déploiement Windows/IIS sont fournis : voir [docs/DEPLOYMENT.fr.md](docs/DEPLOYMENT.fr.md).

## Limites connues

- Les liens non signés (`?user=%user%`) ne sont pas authentifiés. Voir [SECURITY.md](SECURITY.md).
- Un remboursement ou une contestation après un crédit n'est pas répercuté dans PaperCut.
- Le webhook optionnel suit la documentation Worldline mais n'a pas été éprouvé avec une vraie livraison ; le contrôle automatique est le mécanisme de récupération supporté.
- Une seule instance par registre : SQLite sert de stockage mono-nœud.
- Le script de déploiement IIS n'a été exercé que sous Windows Server avec IIS.

## Organisation du dépôt

```
src/PapercutPay          le service
tests/PapercutPay.Tests
deploy/                  script de déploiement IIS
docs/                    guide de déploiement
```

## Licence

Voir [LICENSE](LICENSE).
