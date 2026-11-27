# ucpp-tableparse-feed-test

Regression-tests a locally built `libTableParser.so` against the UCPP regression fixtures of
`ucpp-files-test-automation`, without a cluster. A feed file goes through the same code the pipeline runs,
in one process, and the produced NDF story is compared with the expected one using the same comparison
code as the regression suite.

```
ucpp-files-test-automation   input file, expected story, ComponentsFilter + CommonComparer (the regression diff)
  -> normalizer               NormalizerOperationManager + the manifest's normalization rules
  -> ucpp-enricher            Services/Processor + EnricherOperationManager + the manifest's enrichment rules
     -> ucpp-tableparse       Html/HtmlParser.TableParseUsed (encoding, lock, document wrapping, decoding)
        -> ucpp-parser-table-parse   ParserTableParse (P/Invoke)
           -> ucpp-table-parse-lib   libTableParser.so under test
  -> story producer           StoryProducerOperationManager + the story definition (parser-feedback-content -> NDF)
```

## Requirements

- Linux or WSL with the .NET 8 SDK. The library is a Linux shared object, so the tool cannot run on Windows.
- The repos checked out next to this one:

  ```
  UCPP-TableParse-dotnet/
    ucpp-files-test-automation/
    ucpp-enricher/
    ucpp-tableparse/
    ucpp-parser-table-parse/
    ucpp-table-parse-lib/          with a build in build*/LIBRARY/TABLE_PARSER/libTableParser.so
                                   and .regress_env/ (query sets, utf8 mappings)
    ucpp-tableparse-feed-test/     this repo
  ```
- Access to the Artifactory NuGet feed (`Com.Dowjones.Ucpp.*` packages).

ucpp-enricher, ucpp-tableparse and ucpp-parser-table-parse are project references, so the tool runs your
checkouts. The wrapper's floating NuGet package is not used. The comparison sources from
ucpp-files-test-automation are compiled in as linked files, because `FilesAutomationTest.csproj` does not
build with the .NET 8 SDK (`RegressionTestsComparer.cs`: ambiguous `Split(['\r', '\n'], ...)` under C# 12).

## Run

```bash
cd ~/UCPP-TableParse-dotnet/ucpp-tableparse-feed-test
./run.sh
```

The defaults test `Files/inputs/bwu/bwf_sftppush/20261108962633r1-Tweak-to-BWF_1.xml` against
`Files/int/bwu/bwf_sftppush/outputs/ucpp/20261108962633r1-Tweak-to-BWF_134214003443.txt` with the most
recently built `ucpp-table-parse-lib/build*/LIBRARY/TABLE_PARSER/libTableParser.so`.

```bash
# a specific build of the library
./run.sh --lib ../ucpp-table-parse-lib/build-gcc7/LIBRARY/TABLE_PARSER/libTableParser.so

# another input and its fixture
./run.sh --input    ../ucpp-files-test-automation/FilesAutomationTest/Files/inputs/bwu/bwf_sftppush/20261116045737r1-Tweak-to-BWF_2.xml \
         --expected ../ucpp-files-test-automation/FilesAutomationTest/Files/int/bwu/bwf_sftppush/outputs/ucpp/20261116045737r1-Tweak-to-BWF_134036822701.txt

# keep results of different runs apart
./run.sh --lib ../ucpp-table-parse-lib/build-gcc7/LIBRARY/TABLE_PARSER/libTableParser.so --out out/gcc7

# every BWF regression input
F=../ucpp-files-test-automation/FilesAutomationTest/Files
for i in $F/inputs/bwu/bwf_sftppush/*.xml; do
  b=$(basename "$i" .xml)
  ./run.sh --input "$i" --expected "$(ls $F/int/bwu/bwf_sftppush/outputs/ucpp/${b%_*}_*.txt)"
done
```

| Option | Default |
|---|---|
| `--input <file>` | BWF `20261108962633r1` regression input |
| `--expected <file>` | its int regression output |
| `--lib <file>` | newest `ucpp-table-parse-lib/build*/LIBRARY/TABLE_PARSER/libTableParser.so` |
| `--config-path <dir>` | `ucpp-table-parse-lib/.regress_env/config-gbl` (`PARSER_CONFIG_PATH`) |
| `--database-path <dir>` | `ucpp-table-parse-lib/.regress_env/database` (replaces `/nss/rtns/v_running/database/`) |
| `--manifest <file>` | `Fixtures/int/bwf_manifest_1.0.json` |
| `--story <file>` | `Fixtures/int/bwf_story.json` |
| `--lookups <file>` | `Fixtures/lookups.json` |
| `--provider-symbol`, `--provider-id` | `bwu`, `bwf_sftppush` |
| `--transaction-id <id>` | the `F/UCPPID` of the expected story |
| `--out <dir>` | `out/<input file name>` |
| `--verbose` | echo service logs to the console |

Exit code: `0` all components match, `1` differences, `2` bad arguments, `3` pipeline error, `134` the
native library aborted the process.

## Results

Each run prints one line per component and `PASS`/`FAIL`. Everything is written to `out/<input name>/`
(each run overwrites its folder):

| File | Content |
|---|---|
| `comparison.txt` | full per-component diff (routing, Xml/Ndf version, headline, codes, body) |
| `expected-pbody.xml`, `actual-pbody.xml` | both pbodies after the harness's normalisation: `diff expected-pbody.xml actual-pbody.xml` |
| `actual-story.txt` | the produced NDF story, comparable to the expected `.txt` fixture |
| `tableparse/NN.input.html` | the XHTML that went into each tableparse call (`01` = body, then headline, subheadline, contact) |
| `tableparse/NN.request.txt` | that call's options and the native call sequence |
| `tableparse/NN.text.txt` | the decoded `text` that came out |
| `normalized-data.json`, `enriched-data.json` | the data after each hop |
| `services.log` | everything the services logged |
| `error.txt` | only when the pipeline fails before the comparison |

## How libTableParser.so is used

1. **Manifest.** The enricher rules (`bwf-body-parsed`, headline, subheadline, contact) call the `tableparse`
   operator with `content-type=html`, `is-body=false`, `response-types=text` and
   `arguments=NOSPCCMPR=;RKWIDTH=80;PADTHRESH=5.0;PARSETYPE=bwx;UTFEXT=bwx;TKR=<ticker>`. The operator turns
   them into a `TableParsePayload` and routes it; `Pipeline/InProcessRouter.cs` takes the call instead of the network.
2. **ucpp-tableparse `Html/HtmlParser.cs`.** Encodes non-ASCII characters to `\xx`, wraps the body in an HTML
   document, takes the process-wide lock, then calls the wrapper in a fixed order: `ResetTableParse`,
   `SetTableParseOptions` per argument, `SetQuerySetFile(.../BWX.ER_QUERY_SET)`, `ParseTableData`,
   `GetPlainTextOutput`. Afterwards it decodes base64, `\xx` and HTML entities, and fails on `CANNOT PARSE STORY`.
3. **ucpp-parser-table-parse `ParserTableParse.cs`.** `DllImport`s of `/usr/local/lib/libTableParser.so`:
   `Reset`, `SetTPOptions`, `LoadQuerySetFile`, `ParseTable`, `GetPlainTextOutputSize`/`GetPlainTextOutput`,
   and the ADA/X2E exports.
4. **The library.** `LoadQuerySetFile` reads the query set; `UTFEXT=bwx` loads
   `$PARSER_CONFIG_PATH/utf8-character-mappings.bwx`; `ParseTable` produces the text (`\b` marks preformatted lines).

Recorded sequence of a body call (`tableparse/01.request.txt`):

```
ResetTableParse (native Reset fallback)
SetTableParseOptions NOSPCCMPR=
SetTableParseOptions RKWIDTH=80
SetTableParseOptions PADTHRESH=5.0
SetTableParseOptions PARSETYPE=bwx
SetTableParseOptions UTFEXT=bwx
SetTableParseOptions TKR=NRG
SetQuerySetFile .../ucpp-table-parse-lib/.regress_env/database/PARSER_CONTAINERS/GLOBAL/BWX.ER_QUERY_SET
ParseTableData isBody=False
```

## How the production environment is mapped onto this machine

| Production | Here |
|---|---|
| `/usr/local/lib/libTableParser.so` (wrapper `DllImport`s) | `Native/TableParserLibrary.cs` loads `--lib` and a `DllImportResolver` on the wrapper assembly hands that handle to every import |
| `ResetTableParse` re-opens `/usr/local/lib/libTableParser.so`, then calls `Reset()` | `sudo ln -sfn <--lib> /usr/local/lib/libTableParser.so` lets the wrapper's own reload run (`dlopen` returns the already loaded library). Without it, `Pipeline/LocalParserTableParse.cs` calls `Reset()` of the library under test directly. A link to any other file stops the run |
| `/nss/rtns/v_running/database/PARSER_CONTAINERS/GLOBAL/BWX.ER_QUERY_SET` | `--database-path`; a missing query set fails the run (the wrapper itself ignores the result) |
| `PARSER_CONFIG_PATH` set in the container | `--config-path`, set through libc `setenv` (`Environment.SetEnvironmentVariable` does not reach native `getenv`) |
| ucpp-xmlprocessor pod (`normalize-xml`) | `Pipeline/XmlProcessorEmulator.cs`, a port of `XmlV2/XmlProcessorV2.cs` (unencoded XPath, `Encoding`, `EntireXml`); encoded/HtmlAgilityPack queries are rejected |
| Redis/Dynamo `lookup`, `lookup-exists`, `chaining-code` | `Fixtures/lookups.json` (NYSE and NASDAQ, inferred from the expected codes) and a fixed chaining sequence. They only affect codelines; `E/` codes are dropped by the comparison anyway |

### Checking which library is loaded

The `library` line at the top of each run names the file under test. The dynamic loader confirms it:

```bash
LD_DEBUG=files ./run.sh 2>&1 | grep "libTableParser.so"
```

Only the `--lib` file is opened and initialised. The single attempt to open `/usr/local/lib/libTableParser.so`
comes from the wrapper's `ResetTableParse` and fails when nothing is installed there.

## Status (2026-11-24, int fixtures)

| Input | `build-native` |
|---|---|
| `20261108962633r1-Tweak-to-BWF_1` | body differs, all other components match |
| `20261116045737r1-Tweak-to-BWF_2` | PASS |
| `20261116134495r1-Tweak-to-BWF_3` | PASS |
| `20261116158513r1-Tweak-to-BWF_4` | PASS |
| `20261116520488r1-Tweak-to-BWF_5` | PASS |

- **BWF_1 body.** Only the body's single `<ul class="bwlistdisc">` list (the five "Adjusted EBITDA: Defined as …"
  items) differs. Each `<li>` starts with `\n` and ends with `\n\n`; production rendered those as a space
  (`   --  X`, which the manifest widens to `   --   X`, and a trailing space after the item), the local
  library drops them (`   -- X`). `build-gcc7` produces byte-identical text, so the toolchain is not the cause.
- **`build/` (12:16)** aborts in `LoadQuerySetFile`: `RegExp compilation failed for expression: .*[,- ]*BASIC`
  (exit code 134).

## Fixtures

`Fixtures/int/bwf_manifest_1.0.json` (v0.45) and `Fixtures/int/bwf_story.json` (v0.8) are copied from
`ucpp-management` `configs/int/us/manifests/business-wire/` and `configs/int/us/stories/bw/` at commit
`b099b01`. Refresh them when int changes. Other providers need their own manifest and story, and any
Redis/Dynamo-backed operators they use need an offline stand-in in `Pipeline/Stubs.cs`.
