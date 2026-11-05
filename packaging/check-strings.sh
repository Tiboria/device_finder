#!/usr/bin/env bash
# Device Finder — audit d'internationalisation (critère d'acceptation étape 5.5) :
# aucune chaîne de caractères française ne doit rester dans les sources C#/XAML
# en dehors des dictionnaires de langue et des commentaires de code.
# Les fichiers README/banc (docs) et les dictionnaires (Localization/Strings.*.xaml)
# sont hors périmètre. Cas assumé : l'endonyme « Français » du sélecteur de langue.
#
# Usage : packaging/check-strings.sh   (depuis la racine du dépôt ou n'importe où)
set -u

cd "$(dirname "$0")/.."
ROOT_OK=0

# 1. Littéraux de chaîne C# avec des caractères accentués français
while IFS= read -r match; do
    echo "[FR dans un littéral .cs] $match"
    ROOT_OK=1
done < <(grep -rnI --include="*.cs" --exclude-dir=obj --exclude-dir=bin \
        -E '"[^"]*[àâçéèêëîïôûùüœÀÂÇÉÈÊËÎÏÔÛÙÜŒ][^"]*"' src \
        | grep -v 'Debug.WriteLine' \
        | grep -v '"Français"' \
        | grep -v '^\s*[^:]*:[0-9]*:\s*///' \
        | grep -v ':\s*//.*')

# 2. Attributs XAML avec texte français (hors dictionnaires, hors commentaires,
#    hors valeurs d'expressions {DynamicResource}/{Binding}, endonyme assumé)
while IFS= read -r match; do
    echo "[FR dans un littéral .xaml] $match"
    ROOT_OK=1
done < <(grep -rnI --include="*.xaml" --exclude-dir=Localization \
        -E '(Text|Content|Header|Title|ToolTip|Description)="[^"]*[àâçéèêëîïôûùüœÀÂÇÉÈÊËÎÏÔÛÙÜŒ][^"]*"' src \
        | grep -v '<!--' \
        | grep -v 'Content="Français"' || true)

# 3. XAML : littéraux français résiduels dans MainWindow (dictionnaires exclus)
while IFS= read -r match; do
    echo "[FR XAML générique] $match"
    ROOT_OK=1
done < <(grep -rnI --include="*.xaml" --exclude-dir=Localization \
        -E '>[^<{}]*[àâçéèêëîïôûùüœÀÂÇÉÈÊËÎÏÔÛÙÜŒ]' src \
        | grep -v '<!--' || true)

if [ "$ROOT_OK" -eq 0 ]; then
    echo "OK — aucune chaîne française hors dictionnaires et commentaires."
else
    echo "ÉCHEC — corriger les chaînes listées ci-dessus (les extraire vers Localization/)."
fi
exit "$ROOT_OK"
