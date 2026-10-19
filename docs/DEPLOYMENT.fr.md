# Déploiement sous Windows / IIS

> [English](DEPLOYMENT.md) | Français

PapercutPay s'installe comme une **application IIS distincte** (par exemple `/PapercutPay`) avec son propre pool d'applications. Le script ne touche jamais aux autres applications : une configuration de paiement précédente reste en place et sert de retour arrière.

## Avant de commencer

1. Installer le **Hosting Bundle ASP.NET Core 8** sur le serveur. Le bundle, pas seulement le runtime : c'est lui qui fournit le module IIS. Puis `iisreset` (IIS est interrompu quelques secondes : choisir un moment calme).
2. Utiliser **Windows PowerShell 5.1** (`powershell.exe`) en administrateur. Le module IIS ne se charge pas dans PowerShell 7.
3. Noter le **nom du site IIS** qui doit héberger l'application.
4. Vérifier que le serveur joint Worldline en HTTPS et le port Web Services de PaperCut, et que son adresse figure dans `auth.webservices.allowed-addresses`.
5. Facultatif mais conseillé : sauvegarder la configuration IIS pour la restaurer en une commande :

   ```powershell
   & "$env:windir\system32\inetsrv\appcmd.exe" add backup "avant-papercutpay"
   ```

## Construire le paquet

```powershell
dotnet publish src/PapercutPay --configuration Release --output deploy/package
```

Copier le dossier `deploy` (le script et `package`) sur le serveur, par exemple dans `C:\Temp\PapercutPay-deploy`. Si Windows a marqué les fichiers comme téléchargés, les débloquer (clic droit, Propriétés, Débloquer) ou lancer `Get-ChildItem -Recurse | Unblock-File`.

## Installer

```powershell
cd C:\Temp\PapercutPay-deploy
.\Deploy-PapercutPay.ps1 -SiteName "Default Web Site" -DryRun   # affiche toutes les actions, ne change rien
.\Deploy-PapercutPay.ps1 -SiteName "Default Web Site"
```

Le paramètre facultatif `-LegacyAppName <nom>` fait refuser au script de toucher à une application, un pool ou un dossier existant portant ce nom.

Le script :

- crée le pool `PapercutPay` (sans code managé, toujours démarré, sans arrêt sur inactivité, ce dont le contrôle automatique a besoin) ;
- copie l'application dans `C:\inetpub\PapercutPay` ;
- crée `C:\ProgramData\PapercutPay\` pour le fichier de réglages, la base `payments.db` et les sauvegardes, hors du dossier de l'application pour que les redéploiements n'y touchent jamais, et le réserve aux administrateurs, à SYSTEM et au pool (identifiants universels, donc valable quelle que soit la langue de Windows) ;
- sauvegarde la version précédente avant de la remplacer ;
- peut être relancé sans risque : il garde le fichier de réglages et la base.

## Réglages

Remplir `C:\ProgramData\PapercutPay\appsettings.Production.json` (le script crée un modèle). Voir la [référence de configuration](../README.fr.md#configuration). Au minimum :

- `App:PublicBaseUrl`, `App:LinkSecret`
- `PaperCut:BaseUrl`, `PaperCut:AuthToken`
- `Worldline:MerchantId`, `ApiKeyId`, `ApiSecret` (laisser `BaseUrl` en pré-production tant que vous n'avez pas testé)

Puis recycler le pool et vérifier le service :

```powershell
Restart-WebAppPool PapercutPay
curl.exe -s http://localhost:<port>/PapercutPay/health
```

Attendu : `{"status":"ok","mode":"Live"}`. Si le service ne démarre pas, lire le journal Application de l'Observateur d'événements : il liste les réglages manquants. Le HTTP en clair est refusé partout sauf `/health` (`App:RequireHttps`) : tester le formulaire via l'adresse HTTPS. Si la liaison HTTPS est liée à un nom d'hôte, `https://localhost` ne correspondra pas.

## Recette en pré-production

1. Générer un lien signé pour un compte de test, depuis `C:\inetpub\PapercutPay` :

   ```powershell
   & "$env:ProgramFiles\dotnet\dotnet.exe" .\PapercutPay.dll make-link utilisateur.test 1
   ```

   (Une session ouverte avant l'installation du Hosting Bundle ne connaît pas `dotnet` : utiliser le chemin complet ci-dessus.)
2. Payer le plus petit montant avec une carte de test Worldline. Le solde doit augmenter une fois.
3. Recharger la page de résultat : elle doit indiquer que le paiement est déjà pris en compte.
4. Annuler un paiement avec le bouton de la page Worldline : la commande doit finir `FAILED`.
5. Payer puis fermer l'onglet avant le retour : le crédit doit apparaître seul en une à deux minutes (à vérifier avec la commande `orders`).
6. Annuler le crédit de test par un ajustement dans PaperCut.

## Passage en production

1. Dans le fichier de réglages, mettre `Worldline:BaseUrl` à `https://payment.direct.worldline-solutions.com` et utiliser la clé et le secret d'API de **production**.
2. Recycler le pool et faire une transaction réelle de faible montant ; la rapprocher dans le Merchant Portal et dans PaperCut.
3. Basculer le lien personnalisé PaperCut vers le nouveau service, par exemple `https://print.example.org/PapercutPay/pay?user=%user%`. Le changement est immédiat pour tous les utilisateurs et se défait en remettant l'ancien texte : le noter avant.
4. Après une période de stabilisation, changer le jeton PaperCut et revoir `auth.webservices.allowed-addresses`.

## Mise à jour

Reconstruire le paquet, le copier, relancer le script. Il garde les réglages et la base, sauvegarde la version en place, la remplace et ne redémarre que le pool PapercutPay.

## Retour arrière

```powershell
.\Deploy-PapercutPay.ps1 -SiteName "Default Web Site" -Rollback   # restaure la dernière sauvegarde du code
Stop-WebAppPool PapercutPay                                       # met le service hors ligne
```

La base reste compatible avec les versions précédentes. Pour revenir complètement à une ancienne configuration de paiement : remettre l'ancien lien personnalisé PaperCut et arrêter le pool.

## Reprise manuelle

Lister les dernières commandes (aucun secret affiché) :

```powershell
& "$env:ProgramFiles\dotnet\dotnet.exe" C:\inetpub\PapercutPay\PapercutPay.dll orders 20
```

Les commandes `REVIEW` et `CREDIT_UNCERTAIN` ne sont jamais rejouées automatiquement : comparer le Merchant Portal et le solde PaperCut avant d'agir. `CREDIT_UNCERTAIN` est aussi attribué à une commande restée en `CREDITING` plus de `App:StuckCreditMinutes` (par exemple si le serveur s'est arrêté entre l'appel PaperCut et la mise à jour de la base).
