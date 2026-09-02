#!/usr/bin/env bash
# Download Crystal Reports .rpt test files from known public sources and run the parser
# against each one, reporting success/failure and basic stats.
#
# Sources:
#   https://github.com/benbrahim777/Crystal-Reports  (sample RPT files)
#   https://support.boyum-it.com/hc/en-us/article_attachments/360005864978  (Boyum IT SAP B1 samples, ZIP)
#   https://github.com/souvikduttachoudhury/Crystal-Reports  (SAP Crystal Reports Java SDK samples)
#
# Optional extra source (opt-in, --with-rpt-rs):
#   https://github.com/MrSrsen/rpt-rs  (an independent Rust reader/renderer for the same
#   format; its tests/fixtures/reports tree carries ~160 reports beyond the benbrahim777
#   set already used here). Fetched into tests/rpt-corpus-external/ rather than the main
#   corpus, for two reasons: those files are another project's own test assets and are
#   never committed here, and keeping them out of tests/rpt-corpus/ leaves every corpus
#   count already recorded in BACKLOG.md (0/88 public, 0/2,324 private) comparable.
#
# Usage:
#   ./scripts/download-test-rpts.sh [--download-only] [--test-only] [--with-rpt-rs]
#
# Options:
#   --download-only   Only download; skip running the parser
#   --test-only       Skip download; run parser on whatever is in test/rpt-corpus/
#   --clean           Remove existing corpus before downloading
#   --with-rpt-rs     Also fetch the rpt-rs fixture reports into
#                     tests/rpt-corpus-external/ (not committed; see above)

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
CORPUS_DIR="$REPO_ROOT/tests/rpt-corpus"
EXTERNAL_DIR="$REPO_ROOT/tests/rpt-corpus-external"

DOWNLOAD=true
RUN_TESTS=true
CLEAN=false
WITH_RPT_RS=false

for arg in "$@"; do
    case "$arg" in
        --download-only) RUN_TESTS=false ;;
        --test-only)     DOWNLOAD=false ;;
        --clean)         CLEAN=true ;;
        --with-rpt-rs)   WITH_RPT_RS=true ;;
        *) echo "Unknown option: $arg"; exit 1 ;;
    esac
done

# ---------------------------------------------------------------------------
# Source catalogue
# Each entry: "LABEL|URL"
# URL must be a direct download link to the .rpt file (raw GitHub URL).
# ---------------------------------------------------------------------------
declare -a SOURCES=(
    # benbrahim777/Crystal-Reports — verified RPT files from public repo
    # https://github.com/benbrahim777/Crystal-Reports
    "benbrahim777__BeforeTV|https://raw.githubusercontent.com/benbrahim777/Crystal-Reports/master/BeforeTV.rpt"
    "benbrahim777__BigCells-Mexico|https://raw.githubusercontent.com/benbrahim777/Crystal-Reports/master/Big%20Cells%20-%20Mexico.rpt"
    "benbrahim777__BigCells|https://raw.githubusercontent.com/benbrahim777/Crystal-Reports/master/Big%20Cells.rpt"
    "benbrahim777__Bottom5USA|https://raw.githubusercontent.com/benbrahim777/Crystal-Reports/master/Bottom5USA.rpt"
    "benbrahim777__Canada-CrossTab|https://raw.githubusercontent.com/benbrahim777/Crystal-Reports/master/Canada%20-%20Cross%20Tab.rpt"
    "benbrahim777__ChinaOrders-Grouped-dsct|https://raw.githubusercontent.com/benbrahim777/Crystal-Reports/master/China%20Orders%2C%20Grouped%20with%20dsct.rpt"
    "benbrahim777__ChinaOrders-Grouped|https://raw.githubusercontent.com/benbrahim777/Crystal-Reports/master/China%20Orders%2C%20Grouped.rpt"
    "benbrahim777__ChinaOrders-Percentages|https://raw.githubusercontent.com/benbrahim777/Crystal-Reports/master/China%20Orders%2C%20Percentages.rpt"
    "benbrahim777__ChinaOrders-RunningTotals|https://raw.githubusercontent.com/benbrahim777/Crystal-Reports/master/China%20Orders%2C%20with%20running%20totals.rpt"
    "benbrahim777__Country-Region-Sort|https://raw.githubusercontent.com/benbrahim777/Crystal-Reports/master/Country_Region_CustName_sort.rpt"
    "benbrahim777__CustomerList|https://raw.githubusercontent.com/benbrahim777/Crystal-Reports/master/Customer%20List.rpt"
    "benbrahim777__CustomerOrders-ByCountry|https://raw.githubusercontent.com/benbrahim777/Crystal-Reports/master/Customer%20Orders%2C%20Grouped%20by%20Country.rpt"
    "benbrahim777__Formulas|https://raw.githubusercontent.com/benbrahim777/Crystal-Reports/master/Formulas.rpt"
    "benbrahim777__InventoryStatus|https://raw.githubusercontent.com/benbrahim777/Crystal-Reports/master/Inventory_Status_Report.rpt"
    "benbrahim777__Orders10k|https://raw.githubusercontent.com/benbrahim777/Crystal-Reports/master/Orders10k.rpt"
    "benbrahim777__Orders5-150|https://raw.githubusercontent.com/benbrahim777/Crystal-Reports/master/Orders5-150.rpt"
    "benbrahim777__ProductPriceList|https://raw.githubusercontent.com/benbrahim777/Crystal-Reports/master/Product%20Price%20List.rpt"
    "benbrahim777__ProductPriceList-xs|https://raw.githubusercontent.com/benbrahim777/Crystal-Reports/master/Product%20Price%20List_xs.rpt"
    "benbrahim777__ProductTypeSales-Grouped|https://raw.githubusercontent.com/benbrahim777/Crystal-Reports/master/Product%20Type%20Sales%20by%20Province%2C%20Grouped.rpt"
    "benbrahim777__SalesByCustomer-Grouped|https://raw.githubusercontent.com/benbrahim777/Crystal-Reports/master/Sales%20By%20Customer_grouped.rpt"
    "benbrahim777__TenPct-DiscountDays|https://raw.githubusercontent.com/benbrahim777/Crystal-Reports/master/TenPct%20Discount%20Days.rpt"
    "benbrahim777__Top5-Canadian-Customers|https://raw.githubusercontent.com/benbrahim777/Crystal-Reports/master/Top%205%20Canadian%20Customers.rpt"
    "benbrahim777__Top5-Items-GrossSales|https://raw.githubusercontent.com/benbrahim777/Crystal-Reports/master/Top%205%20Items%20by%20Gross%20Sales.rpt"
    "benbrahim777__Top3-Employee-Sales|https://raw.githubusercontent.com/benbrahim777/Crystal-Reports/master/Top%20Three%20Employee%20Sales.rpt"
    "benbrahim777__Top25USA|https://raw.githubusercontent.com/benbrahim777/Crystal-Reports/master/Top25USA.rpt"
    "benbrahim777__Top5France|https://raw.githubusercontent.com/benbrahim777/Crystal-Reports/master/Top5France.rpt"
    "benbrahim777__Top5USA|https://raw.githubusercontent.com/benbrahim777/Crystal-Reports/master/Top5USA.rpt"
    "benbrahim777__Top5USA-abs|https://raw.githubusercontent.com/benbrahim777/Crystal-Reports/master/Top5USA_abs.rpt"
    "benbrahim777__Top5USA-piechart|https://raw.githubusercontent.com/benbrahim777/Crystal-Reports/master/Top5USA_piechart.rpt"
    "benbrahim777__Top5USAsubCanada|https://raw.githubusercontent.com/benbrahim777/Crystal-Reports/master/Top5USAsubCanada.rpt"
    "benbrahim777__Top5USAunderlay|https://raw.githubusercontent.com/benbrahim777/Crystal-Reports/master/Top5USAunderlay.rpt"
    "benbrahim777__Top5USAwithSub|https://raw.githubusercontent.com/benbrahim777/Crystal-Reports/master/Top5USAwithSub.rpt"
    "benbrahim777__USA-Orders-Pct-colored|https://raw.githubusercontent.com/benbrahim777/Crystal-Reports/master/USA%20Orders%2C%20Percentages%2C%20colored.rpt"
    "benbrahim777__USA-Orders-Pct|https://raw.githubusercontent.com/benbrahim777/Crystal-Reports/master/USA%20Orders%2C%20Percentages.rpt"
    "benbrahim777__USA-Orders-RWB-colored|https://raw.githubusercontent.com/benbrahim777/Crystal-Reports/master/USA%20Orders%2C%20Red%2C%20White%2C%20and%20Blue(color%20orders).rpt"
    "benbrahim777__USA-Orders-RWB-map|https://raw.githubusercontent.com/benbrahim777/Crystal-Reports/master/USA%20Orders%2C%20Red%2C%20White%2C%20and%20Blue(map).rpt"
    "benbrahim777__USA-Orders-RWB|https://raw.githubusercontent.com/benbrahim777/Crystal-Reports/master/USA%20Orders%2C%20Red%2C%20White%2C%20and%20Blue.rpt"
    "benbrahim777__USAvsFrance|https://raw.githubusercontent.com/benbrahim777/Crystal-Reports/master/USAvsFrance.rpt"
    "benbrahim777__USAvsFranceOnDemand|https://raw.githubusercontent.com/benbrahim777/Crystal-Reports/master/USAvsFranceondemand.rpt"
    "benbrahim777__function|https://raw.githubusercontent.com/benbrahim777/Crystal-Reports/master/function.rpt"

    # souvikduttachoudhury/Crystal-Reports — SAP Crystal Reports Java SDK samples
    # https://github.com/souvikduttachoudhury/Crystal-Reports
    # (CrystalReport1.rpt intentionally excluded — it's a blank/stub template with no
    # real sections or content, not useful test material)
    "souvikduttachoudhury__ConsolidatedBalanceSheet|https://raw.githubusercontent.com/souvikduttachoudhury/Crystal-Reports/master/WebContent/Sample%20Reports/Consolidated%20Balance%20Sheet.rpt"
    "souvikduttachoudhury__CustomFunctions|https://raw.githubusercontent.com/souvikduttachoudhury/Crystal-Reports/master/WebContent/Sample%20Reports/Custom%20Functions.rpt"
    "souvikduttachoudhury__CustomerProfileReport|https://raw.githubusercontent.com/souvikduttachoudhury/Crystal-Reports/master/WebContent/Sample%20Reports/Customer%20Profile%20Report.rpt"
    "souvikduttachoudhury__StatementOfAccount|https://raw.githubusercontent.com/souvikduttachoudhury/Crystal-Reports/master/WebContent/Sample%20Reports/Statement%20of%20Account.rpt"
)

# ---------------------------------------------------------------------------
# Download
# ---------------------------------------------------------------------------
if $CLEAN && [[ -d "$CORPUS_DIR" ]]; then
    echo "Cleaning $CORPUS_DIR ..."
    rm -rf "$CORPUS_DIR"
fi

mkdir -p "$CORPUS_DIR"

if $DOWNLOAD; then
    echo "=== Downloading RPT corpus ==="
    for entry in "${SOURCES[@]}"; do
        label="${entry%%|*}"
        url="${entry##*|}"
        dest="$CORPUS_DIR/${label}.rpt"
        if [[ -f "$dest" ]]; then
            echo "  [skip] $label (already exists)"
            continue
        fi
        echo -n "  Downloading $label ... "
        if curl -fsSL --retry 3 --retry-delay 2 -o "$dest" "$url" 2>/dev/null; then
            size=$(wc -c < "$dest")
            echo "OK (${size} bytes)"
            # Sanity-check: Crystal Reports files start with OLE compound header D0 CF 11 E0
            magic=$(xxd -p -l 4 "$dest" 2>/dev/null || true)
            if [[ "$magic" != "d0cf11e0" ]]; then
                echo "  [WARN] $label: unexpected magic bytes ($magic), may not be a valid .rpt file — removing"
                rm -f "$dest"
            fi
        else
            echo "FAILED (HTTP error or not found)"
            rm -f "$dest"
        fi
    done
    echo ""
fi

# ---------------------------------------------------------------------------
# Optional: rpt-rs fixture reports (opt-in, --with-rpt-rs)
#
# One repository tarball rather than ~160 individual requests. Only the reports outside
# its benbrahim777/ directory are extracted: those are the same public files the main
# corpus already has. Kept in tests/rpt-corpus-external/, which .gitignore excludes --
# these are another project's test assets and must not be committed here.
# ---------------------------------------------------------------------------
RPT_RS_TARBALL="https://codeload.github.com/MrSrsen/rpt-rs/tar.gz/refs/heads/main"
RPT_RS_SENTINEL="$EXTERNAL_DIR/.rpt-rs-downloaded"

if $DOWNLOAD && $WITH_RPT_RS && [[ ! -f "$RPT_RS_SENTINEL" ]]; then
    echo "=== Downloading rpt-rs fixture reports (external, not committed) ==="
    mkdir -p "$EXTERNAL_DIR"
    TMP_TAR=$(mktemp /tmp/rpt-rs-XXXXXX.tar.gz)
    if curl -fsSL --retry 3 --retry-delay 2 -o "$TMP_TAR" "$RPT_RS_TARBALL" 2>/dev/null; then
        # Flatten into "<family>__<name>.rpt", matching the main corpus's naming.
        tar -tzf "$TMP_TAR" \
            | grep -E '^[^/]+/tests/(fixtures/reports|meridian)/.*\.rpt$' \
            | grep -v '/benbrahim777/' \
            | while read -r member; do
                rel="${member#*/tests/}"
                flat=$(echo "$rel" | sed -e 's#^fixtures/reports/##' -e 's#^meridian/reports/#meridian_#' \
                                         -e 's#^meridian/#meridian_#' -e 's#/#__#g' -e 's/ /_/g')
                dest="$EXTERNAL_DIR/$flat"
                [[ -f "$dest" ]] && continue
                tar -xzf "$TMP_TAR" -O "$member" > "$dest" 2>/dev/null || { rm -f "$dest"; continue; }
                magic=$(xxd -p -l 4 "$dest" 2>/dev/null || true)
                if [[ "$magic" != "d0cf11e0" ]]; then
                    echo "  [WARN] $flat: unexpected magic bytes ($magic) — removing"
                    rm -f "$dest"
                fi
              done
        count=$(find "$EXTERNAL_DIR" -name '*.rpt' | wc -l | tr -d ' ')
        echo "  Extracted $count report(s) into tests/rpt-corpus-external/"
        touch "$RPT_RS_SENTINEL"
    else
        echo "  [WARN] Failed to download the rpt-rs tarball — skipping."
    fi
    rm -f "$TMP_TAR"
    echo ""
elif $WITH_RPT_RS && [[ -f "$RPT_RS_SENTINEL" ]]; then
    echo "=== rpt-rs fixtures already present in tests/rpt-corpus-external/ (skipping) ==="
    echo ""
fi

# ---------------------------------------------------------------------------
# Boyum IT SAP Business One sample reports (ZIP)
# ---------------------------------------------------------------------------
BOYUM_ZIP_URL="https://support.boyum-it.com/hc/en-us/article_attachments/360005864978"
BOYUM_SENTINEL="$CORPUS_DIR/.boyum-downloaded"

if $DOWNLOAD && [[ ! -f "$BOYUM_SENTINEL" ]]; then
    echo "=== Downloading Boyum IT RPT ZIP ==="
    TMP_ZIP=$(mktemp /tmp/boyum-rpts-XXXXXX.zip)
    if curl -fsSL --retry 3 --retry-delay 2 -o "$TMP_ZIP" "$BOYUM_ZIP_URL" 2>/dev/null; then
        unzip -j "$TMP_ZIP" "Crystal Report Sample Files/*.rpt" -d "$CORPUS_DIR/" 2>&1 | grep inflating || true
        # Rename to boyum__ prefix, normalise spaces
        for f in "$CORPUS_DIR"/*.rpt; do
            base=$(basename "$f")
            if [[ "$base" != *__* ]]; then
                newname="boyum__$(echo "$base" | tr ' ' '_')"
                mv "$f" "$CORPUS_DIR/$newname"
            fi
        done
        touch "$BOYUM_SENTINEL"
        echo "  Boyum IT RPTs installed."
    else
        echo "  [WARN] Failed to download Boyum IT ZIP — skipping."
    fi
    rm -f "$TMP_ZIP"
elif $DOWNLOAD; then
    echo "  [skip] Boyum IT RPTs (already downloaded)"
fi

# Also include any RPT files already in the tests/ tree (sample reports).
#
# rpt-corpus-external is excluded deliberately. This sweep takes anything under tests/,
# and without the exclusion the ~114 third-party reports fetched by --with-rpt-rs get
# symlinked into the main corpus - which silently changes every corpus count the project
# has recorded, the exact thing keeping them in a separate directory is meant to prevent.
find "$REPO_ROOT/tests" -name "*.rpt" ! -path "$CORPUS_DIR/*" ! -path "$EXTERNAL_DIR/*" | while read -r f; do
    fname=$(basename "$f")
    link="$CORPUS_DIR/__local__${fname}"
    if [[ ! -e "$link" ]]; then
        ln -s "$f" "$link" 2>/dev/null || true
    fi
done

# ---------------------------------------------------------------------------
# Run parser
# ---------------------------------------------------------------------------
if $RUN_TESTS; then
    shopt -s nullglob
    files=("$CORPUS_DIR"/*.rpt)
    if [[ ${#files[@]} -eq 0 ]]; then
        echo "No .rpt files found in $CORPUS_DIR"
        echo "Try running with --download-only first, or place .rpt files manually in $CORPUS_DIR/"
        exit 0
    fi

    echo "=== Running corpus tests ($((${#files[@]})) files) ==="
    # The NUnit test RptParser_CorpusFile_ParsesWithoutThrowing auto-discovers all *.rpt
    # files in tests/rpt-corpus/ via CorpusFiles() TestCaseSource.
    dotnet test "$REPO_ROOT/tests/Majorsilence.Crystal.Tests/" \
        --filter "Name~RptParser_CorpusFile" \
        --logger "console;verbosity=normal" \
        2>&1
    rc=$?
    echo ""
    [[ $rc -eq 0 ]] || exit 1
fi
