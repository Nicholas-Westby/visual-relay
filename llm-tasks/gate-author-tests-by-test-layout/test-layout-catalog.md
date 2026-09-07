# Where tests live, per language — catalog for repo-init test-gating defaults

Purpose: let repo-init pick a default gating strategy. The only decision that matters is
**inline vs separate**: a language is INLINE when a typical repo keeps most unit tests inside
the implementation file, which defeats any path-based test/impl split.

Source list: `programming-and-related-languages-expanded.csv` (177 rows), plus 22 rows marked
`(added)`. "(uncertain)" marks claims I would verify before relying on them.

## 1. Language table

| Language | Kind | Test location | Detection signals (ext / marker files) | Test path conventions | Notes |
|---|---|---|---|---|---|
| Python | programming | separate-dir | `.py`; pyproject.toml, setup.py, setup.cfg, tox.ini, Pipfile | `tests/`, `test/`, `test_*.py`, `*_test.py`, `conftest.py` | pytest/unittest. Doctests in docstrings are inline but secondary |
| C | programming | separate-dir | `.c`,`.h`; Makefile, CMakeLists.txt, meson.build, configure.ac | `test/`, `tests/`, `test_*.c`, `*_test.c`, `check_*.c` | Unity, CMocka, Check, Criterion, greatest |
| C++ | programming | separate-dir | `.cpp,.cc,.cxx,.hpp`; CMakeLists.txt, conanfile.*, vcpkg.json, meson.build | `test/`, `tests/`, `*_test.cc`, `*_test.cpp`, `Test*.cpp`, `tst_*.cpp` | GoogleTest, Catch2, doctest. doctest/Catch2 *can* compile into the impl TU; separate is the norm |
| Java | programming | separate-dir | `.java`; pom.xml, build.gradle(.kts), settings.gradle | `src/test/java/**`, `*Test.java`, `*Tests.java`, `Test*.java`, `*IT.java`, `*TestCase.java` | JUnit/TestNG. Surefire also includes the `Test*` prefix; Failsafe uses `*IT` |
| C# | programming | separate-dir | `.cs`; `*.csproj`, `*.sln`, Directory.Build.props | `tests/`, `*.Tests/` and `*.UnitTests/` project dirs, `*Tests.cs`, `*Test.cs` | xUnit/NUnit/MSTest. Test dirs are often `src/Foo.Tests/`, not `tests/` |
| JavaScript | programming | separate-dir | `.js,.mjs,.cjs`; package.json, jest.config.*, vitest.config.* | `__tests__/`, `test/`, `tests/`, `*.test.js`, `*.spec.js`, `e2e/` | Jest/Mocha/Vitest/node:test. Vitest in-source (`import.meta.vitest`) exists but is rare |
| Visual Basic (.NET) | programming | separate-dir | `.vb`; `*.vbproj` | `*Tests.vb`, `*.Tests/` | MSTest/NUnit |
| SQL | query | none | `.sql` | (`tests/*.sql` under dbt) | plain `.sql` files carry no test convention |
| R | programming | separate-dir | `.R,.r`; DESCRIPTION, NAMESPACE, renv.lock | `tests/testthat/test-*.R`, `tests/testthat.R` | testthat. Filename prefix is `test-` (hyphen) |
| Rust | programming | **inline** (mixed) | `.rs`; Cargo.toml, Cargo.lock | in-file `#[cfg(test)] mod tests`, `src/**/tests.rs`, `tests/*.rs`, doctests in `///` | Unit tests inline is idiomatic; `tests/` is integration-only |
| Fortran | programming | separate-dir | `.f90,.F90,.f`; fpm.toml, CMakeLists.txt | `test/`, `test/*_test.f90`, `*.pf` | test-drive, pFUnit, veggies |
| Go | programming | sibling-file | `.go`; go.mod, go.sum | `*_test.go` beside source, `testdata/` | stdlib `testing`; same package but always a separate file |
| Delphi / Object Pascal | programming | separate-dir | `.pas,.dpr`; `*.dproj`, `*.lpi` | `tests/`, `Test*.pas`, `*Tests.pas` | DUnitX/DUnit/FPCUnit; PascalCase `Test` *prefix* is common |
| PHP | programming | separate-dir | `.php`; composer.json, phpunit.xml(.dist) | `tests/`, `*Test.php` | PHPUnit, Pest |
| Scratch | other | none | `.sb3` (binary) | n/a | block language |
| Assembly | programming | uncommon | `.s,.S,.asm`; Makefile | `test/`, `*_test.s` | usually exercised from a C/host harness |
| Ada | programming | separate-dir | `.adb,.ads`; `*.gpr`, alire.toml | `tests/`, `*-tests.adb`, `test_*.adb` | AUnit; child units use `-` (`foo-tests.adb`) |
| Swift | programming | separate-dir | `.swift`; Package.swift, `*.xcodeproj` | `Tests/FooTests/*.swift`, `*Tests.swift`, `*Spec.swift` | XCTest / Swift Testing (`@Test`). Directory is capital `Tests/` |
| Objective-C | programming | separate-dir | `.m,.h`; Podfile, `*.xcodeproj` | `Tests/`, `*Tests.m`, `*Spec.m` | XCTest, Kiwi/Specta |
| COBOL | programming | uncommon | `.cbl,.cob` | `tests/`, `*.cut` | cobol-check, GnuCOBOL harnesses (uncertain) |
| Julia | programming | separate-dir | `.jl`; Project.toml, Manifest.toml | `test/runtests.jl`, `test/*.jl` | stdlib Test. `jldoctest` blocks in docstrings are inline but secondary |
| Ruby | programming | separate-dir | `.rb`; Gemfile, `*.gemspec`, Rakefile | `spec/*_spec.rb`, `test/*_test.rb`, `features/*.feature` | RSpec, Minitest, Cucumber |
| Perl | programming | separate-dir | `.pl,.pm,.t`; cpanfile, Makefile.PL, Build.PL, dist.ini | `t/*.t`, `xt/*.t` | Test::More/Test2. `.t` extension; `xt/` = author tests |
| SAS | programming | uncommon | `.sas` | `tests/` | SASUnit/FUTS are niche (uncertain) |
| Classic Visual Basic | programming | uncommon | `.bas,.frm,.vbp` | n/a | VB6; no mainstream convention |
| Kotlin | programming | separate-dir | `.kt,.kts`; build.gradle.kts, settings.gradle(.kts) | `src/test/kotlin/**`, `*Test.kt`, `*Spec.kt` | JUnit, Kotest |
| MATLAB | programming | separate-dir | `.m`; `*.prj` | `tests/`, `*Test.m`, `test_*.m` | matlab.unittest. `.m` collides with Objective-C |
| Caml | programming | separate-dir | `.ml` | `test/`, `tests/` | see OCaml; Caml Light is legacy (uncertain) |
| Prolog | programming | **inline** (mixed) | `.pl,.pro,.P`; pack.pl | in-file `:- begin_tests(x). ... :- end_tests(x).`, or sidecar `foo.plt` | SWI PlUnit documents embedding tests in the source as the primary style (uncertain) |
| GML | programming | uncommon | `.gml`; `*.yyp` | n/a | GameMaker |
| Lua | programming | separate-dir | `.lua`; `*.rockspec`, `.busted` | `spec/*_spec.lua`, `tests/`, `test_*.lua` | busted, luaunit |
| PowerShell | shell | sibling-file | `.ps1,.psm1,.psd1`; `*.psd1` manifest | `*.Tests.ps1` beside the module or under `tests/` | Pester. Needs case-insensitive `.tests.` matching |
| D | programming | **inline** | `.d,.di`; dub.json, dub.sdl | in-file `unittest { }`; `test/` for integration | `dub test`; unittest blocks live in the implementation file |
| PL/SQL | programming | uncommon | `.pks,.pkb,.sql` | `tests/`, `test_*.pkb`, `ut_*` packages | utPLSQL; tests are DB objects |
| ABAP | programming | sibling-file | `.abap`; `.abapgit.xml` | `*.clas.testclasses.abap` | ABAP Unit local test classes: inline in-system, separate file in an abapGit repo |
| Transact-SQL | query | uncommon | `.sql`; `*.sqlproj` | `tests/`, tSQLt schema objects | tSQLt tests are stored procedures |
| VBScript | programming | uncommon | `.vbs` | n/a | legacy |
| OCaml | programming | separate-dir (mixed) | `.ml,.mli`; dune-project, dune, `*.opam` | `test/`, `tests/`, `*_test.ml`, `test_*.ml` | dune `(test)` is the norm; `ppx_inline_test` (`let%test`) is inline and common in Jane Street code — operator override |
| TypeScript | programming | separate-dir | `.ts`; package.json, tsconfig.json, deno.json | `__tests__/`, `*.test.ts`, `*.spec.ts`, `test/`, `*_test.ts` (Deno) | Jest/Vitest/Mocha/`deno test` |
| Zig | programming | **inline** | `.zig`; build.zig, build.zig.zon | in-file `test "name" { }`; sometimes `src/tests.zig`, `test/` | `zig build test`; tests sit next to the code they test |
| Dart | programming | separate-dir | `.dart`; pubspec.yaml | `test/*_test.dart`, `integration_test/` | package:test, flutter_test |
| X++ | programming | separate-dir | `.xpp` | test model/project, `*Test` classes | SysTest (D365 F&O); environment-bound (uncertain) |
| Lisp | programming | separate-dir | `.lisp,.lsp,.asd`; `*.asd`, qlfile | `t/`, `tests/`, `*-test.lisp`, `*-tests.lisp` | FiveAM, Parachute, Rove; ASDF `test-op` |
| Scala | programming | separate-dir | `.scala,.sc`; build.sbt, build.sc, project/ | `src/test/scala/**`, `*Spec.scala`, `*Suite.scala`, `*Test.scala` | ScalaTest, munit, specs2, ZIO Test |
| LabVIEW | other | uncommon | `.vi,.lvproj` (binary) | n/a | VI Tester; GUI-bound |
| Ladder Logic | other | uncommon | `.L5X`, vendor formats | n/a | PLC vendor tooling |
| VHDL | shader/hardware | separate-dir | `.vhd,.vhdl` | `tb/`, `testbench/`, `*_tb.vhd`, `test/` | VUnit, OSVVM, cocotb |
| XSLT | template | uncommon | `.xsl,.xslt` | `test/`, `*.xspec` | XSpec (uncertain) |
| Haskell | programming | separate-dir | `.hs`; `*.cabal`, stack.yaml, package.yaml | `test/`, `test/Spec.hs`, `*Spec.hs`, `test/Main.hs` | hspec/tasty/QuickCheck; `doctest` in Haddock comments is secondary |
| (Visual) FoxPro | programming | uncommon | `.prg,.scx` | n/a | legacy (uncertain) |
| ActionScript | programming | separate-dir | `.as`; `*.as3proj` | `test/`, `*Test.as` | FlexUnit; legacy (uncertain) |
| Apex | programming | sibling-file | `.cls,.trigger`; sfdx-project.json | `*Test.cls`, `Test*.cls`, `*_Test.cls` in `force-app/main/default/classes/` | `@isTest` classes sit in the same directory as implementation classes |
| AppleScript | shell | uncommon | `.applescript,.scpt` | n/a | none mainstream |
| Awk | shell | uncommon | `.awk` | `test/` + `*.ok` golden files | gawk's own suite |
| Bash | shell | separate-dir | `.sh,.bash`; no marker file | `test/`, `tests/`, `*.bats`, `*_test.sh`, `test_*.sh`, `spec/*_spec.sh` | bats-core, shunit2, shellspec |
| bc | other | uncommon | `.bc` | n/a | - |
| BCPL | programming | uncommon | `.b` | n/a | historic |
| Bourne shell | shell | separate-dir | `.sh` | as Bash | see Bash |
| C shell | shell | uncommon | `.csh` | n/a | - |
| CFML | programming | separate-dir | `.cfc,.cfm`; box.json, server.json | `tests/`, `*Test.cfc`, `*Spec.cfc` | TestBox (uncertain) |
| CL (OS/400) | shell | uncommon | `.clle,.cl` | n/a | IBM i (uncertain) |
| Clojure | programming | separate-dir | `.clj,.cljs,.cljc`; deps.edn, project.clj, shadow-cljs.edn | `test/**/*_test.clj`, `test/` | clojure.test, kaocha. `with-test`/`:test` metadata allow inline but are rare |
| CoffeeScript | programming | separate-dir | `.coffee`; package.json | `test/*.coffee`, `*.spec.coffee` | Mocha (legacy) |
| cT | other | uncommon | - | n/a | obsolete teaching language |
| ECMAScript | programming | separate-dir | see JavaScript | see JavaScript | alias for JavaScript |
| EGL | programming | uncommon | `.egl` | n/a | IBM EGL (uncertain) |
| Elixir | programming | separate-dir | `.ex,.exs`; mix.exs, mix.lock | `test/**/*_test.exs`, `test/test_helper.exs` | ExUnit. Doctests are inline in `@doc` but are opt-in one-liners |
| Erlang | programming | separate-dir (mixed) | `.erl,.hrl`; rebar.config, erlang.mk | `test/*_SUITE.erl`, `test/*_tests.erl`, `src/*_tests.erl` | Common Test + EUnit. EUnit `*_test()` functions *can* live in the module behind `-ifdef(TEST)` — flag for override |
| F# | programming | separate-dir | `.fs,.fsx,.fsi`; `*.fsproj`, paket.dependencies | `tests/`, `*Tests.fs`, `Tests.fs` | Expecto, xUnit, FsCheck |
| GAMS | other | uncommon | `.gms` | n/a | optimization modelling |
| Groovy | programming | separate-dir | `.groovy,.gradle`; build.gradle | `src/test/groovy/**`, `*Test.groovy`, `*Spec.groovy` | Spock uses `*Spec.groovy` |
| Io | programming | uncommon | `.io` | `tests/` | (uncertain) |
| J | programming | uncommon | `.ijs` | n/a | - |
| J# | programming | uncommon | `.jsl` | n/a | discontinued |
| JScript | programming | uncommon | `.js` (WSH) | n/a | legacy |
| Logo | other | uncommon | `.lgo` | n/a | - |
| ML | programming | separate-dir | `.sml,.sig`; `*.cm`, `*.mlb` | `test/`, `tests/` | SML/NJ, MLton; SMLUnit is niche (uncertain) |
| MS-DOS batch | shell | uncommon | `.bat,.cmd` | n/a | - |
| NetLogo | other | uncommon | `.nlogo` | n/a | BehaviorSpace is not unit testing |
| Nim | programming | separate-dir | `.nim,.nims`; `*.nimble` | `tests/t*.nim`, `tests/` | `nimble test` runs `tests/t*.nim`. `when isMainModule` and `runnableExamples` are inline but secondary |
| OpenCL | shader/hardware | uncommon | `.cl` | host-side `test/` | tested from a C/C++ host |
| PL/I | programming | uncommon | `.pli` | n/a | legacy |
| PowerScript | programming | uncommon | `.srw,.pbl` | n/a | PowerBuilder (uncertain) |
| Pure Data | other | none | `.pd` | n/a | patch language |
| PureBasic | programming | uncommon | `.pb,.pbi` | n/a | - |
| Q | programming | uncommon | `.q` | `tests/`, `*.t` | k4unit/qunit are niche (uncertain) |
| REBOL | programming | uncommon | `.r,.reb` | `tests/` | (uncertain) |
| Ring | programming | uncommon | `.ring` | `tests/` | (uncertain) |
| Scheme | programming | separate-dir | `.scm,.ss,.sld` | `tests/`, `test/`, `*-test.scm` | SRFI-64; varies per implementation (uncertain) |
| Solidity | programming | separate-dir | `.sol`; foundry.toml, hardhat.config.*, truffle-config.js | `test/*.t.sol` (Foundry), `test/*.ts|js` (Hardhat) | forge-std. `*.t.sol` filename pattern |
| Structured Text | other | uncommon | `.st,.exp` | `*_test` POUs | TcUnit and friends are vendor-bound (uncertain) |
| Tcl | programming | separate-dir | `.tcl`; pkgIndex.tcl | `tests/*.test` | tcltest. Uses the `.test` *extension*, not a `_test` stem |
| tcsh | shell | uncommon | `.tcsh,.csh` | n/a | - |
| thinBasic | programming | uncommon | `.tbasic` | n/a | - |
| V | programming | sibling-file | `.v`; v.mod | `*_test.v` beside source, `tests/` | `v test`; `fn test_x()` in `_test.v`. `.v` collides with Verilog and Coq |
| XBase++ | programming | uncommon | `.prg` | n/a | (uncertain) |
| XC | programming | uncommon | `.xc` | n/a | XMOS (uncertain) |
| Xojo | programming | uncommon | `.xojo_*` | XojoUnit project items | IDE-bound (uncertain) |
| XPL | programming | uncommon | `.xpl` | n/a | historic |
| Z shell | shell | uncommon | `.zsh` | `tests/`, `*.zunit` | zunit, bats (uncertain) |
| HTML | markup/style | none | `.html`; index.html | `tests/`, `e2e/`, `cypress/e2e/`, `*.spec.ts` | tests live in a JS runner's directory (Playwright/Cypress), never in `.html` |
| CSS | markup/style | none | `.css` | `tests/`, `e2e/` | visual-regression / e2e only |
| XML | data/config | none | `.xml` | n/a | - |
| Markdown | markup/style | none | `.md` | n/a | code fences can be doctested (rustdoc, mdbook, pytest-markdown) — inline, but not a test file |
| Sass | markup/style | none | `.sass` | `test/` (sass-true) | rare |
| SCSS | markup/style | none | `.scss` | `test/*.scss` (sass-true) | rare |
| Less | markup/style | none | `.less` | n/a | - |
| Stylus | markup/style | none | `.styl` | n/a | - |
| JSX | programming | separate-dir | `.jsx`; package.json | `__tests__/`, `*.test.jsx`, `*.spec.jsx` | Testing Library + Jest/Vitest |
| TSX | programming | separate-dir | `.tsx`; package.json, tsconfig.json | `__tests__/`, `*.test.tsx`, `*.spec.tsx` | Testing Library + Jest/Vitest |
| GLSL | shader/hardware | none | `.glsl,.vert,.frag` | golden-image `tests/` | tested via a host harness |
| GLSL ES | shader/hardware | none | `.glsl,.vert,.frag` | as GLSL | - |
| HLSL | shader/hardware | none | `.hlsl,.fx` | as GLSL | - |
| WGSL | shader/hardware | none | `.wgsl` | host `tests/` | wgpu CTS is host-driven |
| Metal Shading Language | shader/hardware | none | `.metal` | host `Tests/` | - |
| CUDA C++ | programming | separate-dir | `.cu,.cuh`; CMakeLists.txt | `test/`, `*_test.cu` | GoogleTest from the C++ side; needs a GPU to run |
| Verilog | shader/hardware | separate-dir | `.v,.vh` | `tb/`, `*_tb.v`, `test/` | testbenches; cocotb tests are Python `test_*.py` |
| SystemVerilog | shader/hardware | separate-dir | `.sv,.svh` | `tb/`, `*_tb.sv`, `*_unit_test.sv` | UVM, SVUnit, cocotb |
| GraphQL | query | none | `.graphql,.gql` | n/a | schema tested from the host language |
| SPARQL | query | none | `.rq,.sparql` | `tests/` (W3C manifests) | - |
| Cypher | query | none | `.cypher,.cql` | n/a | - |
| PromQL | query | none | embedded in `.yml` rules | `tests/*.yml` (`promtool test rules`) | unit tests are YAML files, not PromQL files |
| Kusto Query Language (KQL) | query | none | `.kql,.csl` | n/a | - |
| DAX | query | none | in `.pbix`/`.bim` | n/a | - |
| Power Query M | query | none | `.pq,.m` | `*.query.pq` (uncertain) | - |
| MDX | query | none | `.mdx` | n/a | OLAP MDX. If this row means MDX-the-Markdown-flavor, treat as markup with `*.test.tsx` companions |
| Datalog | query | none | `.dl` | `tests/` (Souffle) | (uncertain) |
| HCL | build/infra | separate-dir | `.tf,.hcl`; `*.tf`, .terraform.lock.hcl | `tests/*.tftest.hcl`, `test/*_test.go` (Terratest), `examples/` | native `terraform test` since 1.6 |
| Nix | build/infra | uncommon | `.nix`; flake.nix, default.nix, shell.nix | `tests/`, `checks` attr, `nixos/tests/*.nix` | tests are derivations; not gateable by path |
| Bicep | build/infra | uncommon | `.bicep`; bicepconfig.json | `tests/*.bicep`, `*.tests.bicep` | Azure test framework is experimental (uncertain) |
| CUE | data/config | uncommon | `.cue`; cue.mod/ | `*_test.cue` (uncertain) | assertions are usually inline constraints |
| Dhall | data/config | uncommon | `.dhall` | `*Test.dhall` (uncertain) | `assert :` expressions are inline |
| Jsonnet | data/config | uncommon | `.jsonnet,.libsonnet`; jsonnetfile.json | `tests/`, `*_test.jsonnet` | - |
| YAML | data/config | none | `.yml,.yaml` | n/a | - |
| JSON | data/config | none | `.json` | n/a | fixtures only |
| TOML | data/config | none | `.toml` | n/a | - |
| Make | build/infra | none | Makefile, `*.mk`, GNUmakefile | a `test:` target | declares tests written in other languages |
| CMake | build/infra | none | CMakeLists.txt, `*.cmake` | `Tests/`, CTest `add_test()` | drives C/C++ tests |
| Starlark | build/infra | none | `.bzl,.star`; WORKSPACE, MODULE.bazel, BUILD.bazel | `tests/`, `*_test.bzl`, `*_tests.bzl` | unittest.bzl; mostly declares tests in other languages |
| APL | programming | uncommon | `.apl,.dyalog` | `tests/` | (uncertain) |
| BASIC | programming | uncommon | `.bas` | n/a | - |
| Forth | programming | uncommon | `.fth,.4th` | `test/`, `*-test.fth` | ANS-style `T{ ... -> ... }T` cases, usually in separate files (uncertain) |
| Smalltalk | programming | separate-dir | `.st`, Tonel `*.class.st`; `.project` | `src/Foo-Tests/*Test.class.st` | SUnit. Classically image-resident; Tonel/Filetree export puts tests in `*-Tests` package dirs |
| Racket | programming | **inline** (mixed) | `.rkt`; info.rkt | in-file `(module+ test ...)`, plus `tests/`, `*-test.rkt` | `raco test` runs a file's `test` submodule — the documented default |
| Raku | programming | separate-dir | `.raku,.rakumod,.p6`; META6.json | `t/*.t`, `xt/*.t` | Test module |
| Elm | programming | separate-dir | `.elm`; elm.json | `tests/*.elm`, `tests/Tests.elm` | elm-test |
| Crystal | programming | separate-dir | `.cr`; shard.yml | `spec/*_spec.cr`, `spec/spec_helper.cr` | stdlib Spec |
| Hack | programming | separate-dir | `.hack,.php`; .hhconfig | `tests/`, `*Test.php` | HackTest (uncertain) |
| GDScript | programming | separate-dir | `.gd`; project.godot | `test/test_*.gd`, `tests/` | GUT, gdUnit4 |
| QML | programming | separate-dir | `.qml`; CMakeLists.txt, `*.pro` | `tests/auto/`, `tst_*.qml` | Qt Quick Test. `tst_` prefix |
| Chapel | programming | separate-dir | `.chpl`; Mason.toml | `test/*.chpl` + `*.good` expected output | `mason test` / `start_test` (uncertain) |
| Mojo | programming | separate-dir | `.mojo,.🔥`; mojoproject.toml | `test/test_*.mojo` | `mojo test` (uncertain, fast-moving) |
| Gleam | programming | separate-dir | `.gleam`; gleam.toml | `test/*_test.gleam` | gleeunit |
| ReScript | programming | separate-dir | `.res,.resi`; rescript.json / bsconfig.json | `tests/`, `__tests__/`, `*_test.res` | runs on JS test runners (uncertain) |
| ReasonML | programming | separate-dir | `.re,.rei`; dune-project, bsconfig.json | `test/`, `__tests__/` | as OCaml / JS |
| Pony | programming | sibling-file | `.pony`; corral.json | `_test.pony` inside the package, `*/test/main.pony` | PonyTest; the stdlib keeps `_test.pony` beside implementation files |
| Fish shell | shell | uncommon | `.fish` | `tests/*.fish` + `.expected` | fishtape (uncertain) |
| Nushell | shell | uncommon | `.nu` | `tests/`, `test_*` commands | std assert / nutest; `test_*` commands may sit in the implementation file (uncertain) |
| Vyper | programming | separate-dir | `.vy`; ape-config.yaml, foundry.toml | `tests/test_*.py` | tests are written in Python (pytest / Titanoboa) |
| Move | programming | separate-dir (mixed) | `.move`; Move.toml | `tests/*.move`; in-module `#[test]` / `#[test_only]` fns | Sui/Aptos layout is `sources/` + `tests/`, but in-module `#[test]` is common — flag for override (uncertain) |
| OpenSCAD | programming | uncommon | `.scad` | `tests/` + golden images | - |
| WebAssembly Text (WAT) | programming | uncommon | `.wat,.wast` | `test/*.wast` | spec-test scripts |
| Protocol Buffers | data/config | none | `.proto`; buf.yaml, buf.gen.yaml | n/a | conformance tests live in host languages |
| Thrift IDL | data/config | none | `.thrift` | n/a | - |
| Razor | template | none | `.cshtml,.razor` | `*Tests.cs` (bUnit) | bUnit tests are C#; `.razor` test components exist (uncertain) |
| Jinja | template | none | `.j2,.jinja` | n/a | tested from Python |
| Liquid | template | none | `.liquid` | `test/` (theme-check) | - |
| Handlebars | template | none | `.hbs` | n/a | - |
| Mustache | template | none | `.mustache` | spec `.json` suites | - |
| FreeMarker | template | none | `.ftl` | `*Test.java` | - |
| Apache Velocity | template | none | `.vm` | `*Test.java` | - |
| ERB | template | none | `.erb` | `spec/` view specs (Ruby) | - |
| Twig | template | none | `.twig` | `tests/*Test.php` | - |
| Pug | template | none | `.pug` | n/a | - |
| Haml | template | none | `.haml` | `spec/` | - |
| TeX | markup/style | uncommon | `.tex,.sty`; latexmkrc, build.lua | `testfiles/*.lvt` + `*.tlg` | l3build |
| LaTeX | markup/style | uncommon | `.tex,.cls`; `*.ins`, build.lua | `testfiles/*.lvt` | l3build |
| Svelte (added) | programming | separate-dir | `.svelte`; svelte.config.js, package.json | `*.test.ts`, `*.spec.ts`, `src/**/__tests__/`, `e2e/` | Vitest + testing-library/svelte; Playwright in `e2e/` |
| Vue SFC (added) | programming | separate-dir | `.vue`; vite.config.*, package.json | `__tests__/*.spec.ts`, `*.spec.ts`, `tests/unit/` | Vitest/Jest + @vue/test-utils |
| Astro (added) | programming | separate-dir | `.astro`; astro.config.mjs | `src/**/*.test.ts`, `e2e/` | Vitest / Playwright (uncertain) |
| Terraform module (added) | build/infra | separate-dir | `.tf`; `*.tf`, .terraform.lock.hcl, versions.tf | `tests/*.tftest.hcl`, `test/*_test.go`, `examples/` | native `terraform test`; Terratest is Go |
| Ansible (added) | data/config | separate-dir | `.yml` + `roles/`, ansible.cfg, galaxy.yml, playbooks | `molecule/*/converge.yml` + `verify.yml`, `roles/*/tests/`, `tests/` | Molecule; test dir is `molecule/` |
| Dockerfile (added) | build/infra | none | `Dockerfile`, `*.dockerfile`, compose.yaml | `tests/` (container-structure-test YAML), goss files | - |
| Odin (added) | programming | sibling-file (mixed) | `.odin`; ols.json | `tests/`, `*_test.odin`, `@(test)` procs | `odin test <pkg>` runs every `@(test)` proc in the package; usually collected into `_test.odin` files (uncertain) |
| Roc (added) | programming | **inline** | `.roc`; main.roc | in-file `expect` / `expect-fx` blocks | `roc test` runs the module's `expect`s (uncertain, pre-1.0) |
| Cairo (added) | programming | **inline** (mixed) | `.cairo`; Scarb.toml | in-file `#[cfg(test)] mod tests`; `tests/*.cairo` | snforge / `scarb test`; copies Rust's layout |
| Haxe (added) | programming | separate-dir | `.hx`; haxelib.json, `*.hxml` | `test/`, `tests/`, `Test*.hx`, `*Test.hx` | utest, munit |
| Vala (added) | programming | separate-dir | `.vala,.vapi`; meson.build | `tests/` | GLib.Test |
| Lean 4 (added) | programming | separate-dir (mixed) | `.lean`; lakefile.lean / lakefile.toml, lean-toolchain | `test/`, `tests/`, `Test/`; in-file `#guard`, `example` | mathlib-style repos use `test/`, but in-file assertions are normal — operator override likely (uncertain) |
| Idris 2 (added) | programming | separate-dir | `.idr`; `*.ipkg` | `tests/` with `expected` golden files | - |
| Agda (added) | programming | separate-dir (mixed) | `.agda,.lagda`; `*.agda-lib` | `test/` | type-checking is the test; `test/` holds regressions |
| Coq / Rocq (added) | programming | separate-dir | `.v`; _CoqProject, dune-project | `test-suite/`, `tests/` | `.v` collides with Verilog and V |
| Objective-C++ (added) | programming | separate-dir | `.mm`; `*.xcodeproj`, Podfile | `Tests/`, `*Tests.mm` | XCTest |
| Kotlin Multiplatform (added) | programming | separate-dir | `.kt`; build.gradle.kts with `kotlin { }` targets | `src/commonTest/kotlin/**`, `src/jvmTest/**`, `src/androidUnitTest/**`, `src/iosTest/**` | camelCase source-set directories |
| Jupyter notebook (added) | other | uncommon | `.ipynb`; requirements.txt, environment.yml | `tests/test_*.py`; nbval / nbmake run the notebooks themselves | cells are not test files; testbook exists |
| Gherkin / Cucumber (added) | other | separate-dir | `.feature`; cucumber.yml, behave.ini | `features/*.feature`, `features/step_definitions/`, `tests/features/` | Cucumber, behave, SpecFlow, godog |
| Robot Framework (added) | other | separate-dir | `.robot,.resource`; `*.robot` | `tests/*.robot`, `atests/`, `utests/` | - |
| Emacs Lisp (added) | programming | separate-dir | `.el`; Cask, Eldev, `*-pkg.el` | `test/*-test.el`, `test/*-tests.el` | ERT |
| dbt (SQL) (added) | data/config | separate-dir | dbt_project.yml, profiles.yml, `models/` | `tests/*.sql`, `models/**/schema.yml` | data tests via `dbt test` |

## 2. Classification for the init code

Partition of all 199 rows. Only INLINE membership changes the default gating strategy.

### INLINE (7) — same-file unit tests; path-based test/impl separation cannot work

Rust, Zig, D, Racket, Cairo (added), Roc (added), Prolog (uncertain — SWI PlUnit documents
embedded `begin_tests/end_tests` blocks as the primary style, with `foo.plt` as the alternative).

Deliberately **not** here, though each has a real inline mechanism that an operator may need to
override: OCaml (`ppx_inline_test`), Erlang (EUnit `*_test()` behind `-ifdef(TEST)`), Move
(in-module `#[test]`), Nim (`when isMainModule`, `runnableExamples`), Lean 4 (`#guard`/`example`),
C++ (doctest in the implementation TU), Nushell, JS/TS (Vitest in-source), Odin (`@(test)` procs
anywhere in the package).

### SEPARATE (82) — path classifier can gate

Python, C, C++, Java, C#, JavaScript, Visual Basic (.NET), R, Fortran, Go, Delphi / Object Pascal,
PHP, Ada, Swift, Objective-C, Julia, Ruby, Perl, Kotlin, MATLAB, Caml, Lua, PowerShell, ABAP,
OCaml, TypeScript, Dart, X++, Lisp, Scala, Haskell, ActionScript, Apex, Bash, Bourne shell, CFML,
Clojure, CoffeeScript, ECMAScript, Elixir, Erlang, F#, Groovy, ML, Nim, Scheme, Solidity, Tcl, V,
JSX, TSX, CUDA C++, Smalltalk, Raku, Elm, Crystal, Hack, GDScript, QML, Chapel, Mojo, Gleam,
ReScript, ReasonML, Pony, Vyper, Move, Svelte (added), Vue SFC (added), Astro (added),
Odin (added), Haxe (added), Vala (added), Lean 4 (added), Idris 2 (added), Agda (added),
Coq / Rocq (added), Objective-C++ (added), Kotlin Multiplatform (added), Gherkin / Cucumber (added),
Robot Framework (added), Emacs Lisp (added).

### NONE (110) — never decides the strategy

SQL, Scratch, Assembly, COBOL, SAS, Classic Visual Basic, PL/SQL, Transact-SQL, VBScript, LabVIEW,
Ladder Logic, VHDL, XSLT, (Visual) FoxPro, AppleScript, Awk, bc, BCPL, C shell, CL (OS/400), cT,
EGL, GAMS, GML, Io, J, J#, JScript, Logo, MS-DOS batch, NetLogo, OpenCL, PL/I, PowerScript,
Pure Data, PureBasic, Q, REBOL, Ring, Structured Text, tcsh, thinBasic, XBase++, XC, Xojo, XPL,
Z shell, HTML, CSS, XML, Markdown, Sass, SCSS, Less, Stylus, GLSL, GLSL ES, HLSL, WGSL,
Metal Shading Language, Verilog, SystemVerilog, GraphQL, SPARQL, Cypher, PromQL, KQL, DAX,
Power Query M, MDX, Datalog, HCL, Nix, Bicep, CUE, Dhall, Jsonnet, YAML, JSON, TOML, Make, CMake,
Starlark, APL, BASIC, Forth, Fish shell, Nushell, OpenSCAD, WAT, Protocol Buffers, Thrift IDL,
Razor, Jinja, Liquid, Handlebars, Mustache, FreeMarker, Apache Velocity, ERB, Twig, Pug, Haml, TeX,
LaTeX, Terraform module (added), Ansible (added), Dockerfile (added), Jupyter notebook (added),
dbt (SQL) (added).

A repository made only of NONE languages (a static HTML/CSS site, a Terraform module, an Ansible
collection) still gets the **separate-file strategy with no language-specific extras** — its tests
land in `tests/`, `e2e/`, `molecule/` or `cypress/e2e/` and are perfectly gateable by path.
Several NONE members do carry real test-path conventions worth feeding the classifier even though
they never flip the strategy: HCL/Terraform, Ansible, Verilog/SystemVerilog/VHDL, PromQL, Starlark,
CMake, dbt, Bash-adjacent shells.

## 3. Detection algorithm for mixed-language repositories

1. **Enumerate tracked files** with `git ls-files` (never a filesystem walk: it drags in build
   output). Drop any path with a segment in: `node_modules`, `vendor`, `third_party`, `thirdparty`,
   `dist`, `build`, `out`, `target`, `bin`, `obj`, `.venv`, `venv`, `site-packages`, `Pods`,
   `Carthage`, `bower_components`, `.terraform`, `_build`, `deps`, `generated`, `gen`,
   `.gradle`, `.next`, `.svelte-kit`, `coverage`. Drop lockfiles and generated files
   (`*.lock`, `package-lock.json`, `yarn.lock`, `Cargo.lock`, `*.pb.go`, `*.g.dart`, `*_pb2.py`,
   `*.generated.*`).
2. **Strong signal — marker files.** Scan the repo root and every directory up to depth 3 (monorepos
   put `Cargo.toml`/`package.json` under `packages/*`). A marker file present ⇒ that language is
   *in* the repo regardless of file counts. Column 4 of the table is the marker list; the ones that
   matter for the inline decision are `Cargo.toml` (Rust), `build.zig` / `build.zig.zon` (Zig),
   `dub.json` / `dub.sdl` (D), `info.rkt` (Racket), `Scarb.toml` (Cairo), `main.roc` (Roc),
   `pack.pl` (SWI-Prolog).
3. **Weak signal — extension counts.** Count surviving tracked files per extension. A language with
   no marker file counts as present only above a floor: `>= 3` files, or `>= 2%` of tracked source
   files. Keep the floor low for INLINE languages — one `.rs` file with `#[cfg(test)] mod tests` is
   enough to break path gating — but not zero, so a stray sample in `docs/` cannot flip a repo.
4. **Disambiguate colliding extensions** before deciding: `.m` = Objective-C (with `*.xcodeproj` /
   `Podfile` / a sibling `.h`) vs MATLAB (`*.prj`, `+package/` dirs) vs Power Query M.
   `.v` = Verilog vs Coq (`_CoqProject`) vs V (`v.mod`). `.pl` = Perl (`cpanfile`, `Makefile.PL`,
   `t/`) vs Prolog (`:- ` directives, `pack.pl`) — this one decides an INLINE membership, so when
   ambiguous, sniff file content for `:- module(` / `:- begin_tests(` before treating `.pl` as
   inline-capable. `.st` = Smalltalk vs Structured Text. `.d` = D vs Makefile depfiles (ignore `.d`
   files sitting next to a same-stem `.o`).
5. **Derive the per-extension inline list**, not a per-repo boolean. Map each detected INLINE
   language to its extensions and union them:
   `rust -> .rs`, `zig -> .zig`, `d -> .d,.di`, `racket -> .rkt,.rktl`, `cairo -> .cairo`,
   `roc -> .roc`, `prolog -> .pl,.pro,.P,.plt`.
   Write that union to the repo config, e.g.
   `testGating: { strategy: "path", inlineTestExtensions: [".rs"], overriddenBy: null }`.
   A Rust + Python repo therefore gets `[".rs"]`: `.py` edits stay path-gated normally, while a
   `.rs` edit is treated as possibly-test-bearing. Per-extension is the whole point — a repo-wide
   "inline" flag would give up gating on the Python side of the same repo for no reason.
6. **Gate behaviour per file.** For a file whose extension is in `inlineTestExtensions`, the
   revert-and-verify step cannot separate test from fix, so pick one of: (a) skip the red-test
   proof for that file and fall back to "tests must be red before the fix commit, verified by
   running the suite at the pre-fix commit"; (b) require the test stage to put the new tests in the
   language's separate location (`tests/` for Rust/Cairo, a `_test.zig`/`test/` file for Zig) and
   gate normally; (c) mark the run un-gated and say so. Whichever is chosen, record it in the
   config so the operator can override.
7. **Doctests are a cross-cutting hazard even for SEPARATE languages** — Python `>>>`, Rust `///`,
   Elixir `iex>`, Julia `jldoctest`, Haskell `>>>`, Nim `runnableExamples`, Markdown code fences.
   The test-writing stage should be instructed not to add doctests when path gating is on, because
   they land in implementation files and will be reverted.
8. **Nothing detected** (empty repo, only NONE languages, or every count below the floor): write
   `strategy: "path"`, `inlineTestExtensions: []`, `detected: []`. That is the correct default for a
   static site or an IaC repo — its Playwright/Molecule tests live in a directory.

## 4. Test path patterns the stated generic classifier would MISS

Rules assumed: dirs named exactly `test|tests|spec|specs|t|__tests__|unittests`; dir segments
starting `test-|tests-|test_` or ending `-test|-tests|_test|_tests`; filenames containing
`.tests.|.spec.|.test.`; stems ending `_test|-test|_spec` or starting `test_`; PascalCase stems
ending `Test|Tests|Spec` for C#/Java/Kotlin/Swift-like.

**Cross-cutting first: make every rule case-insensitive.** As written they miss `Tests/` (Swift
SwiftPM), `*.Tests.ps1` (Pester), `Test_Foo.cls` (Apex), `Foo-Tests/` (Smalltalk Tonel).

| Missed pattern | Language(s) |
|---|---|
| stem prefix `test-` (`test-foo.R`) | R (testthat) |
| stem suffixes `-tests`, `_tests`, `-spec`, `-specs`, `_specs` (`foo-tests.lisp`, `foo-tests.adb`, `*-tests.el`, `foo-spec.js`) | Common Lisp, Ada, Emacs Lisp, JS/Clojure styles |
| stem exactly `test`, `tests`, `spec` (`src/tests.rs`, `src/foo/tests.rs`, `test/Spec.hs`) | Rust, Haskell |
| stem `runtests` (`test/runtests.jl`) | Julia |
| stem prefix `tst_` (`tst_foo.qml`, `tst_foo.cpp`) | QML, Qt C++ |
| stem prefix `t` + digits/name (`tests/tfoo.nim`) | Nim |
| stem suffix `_SUITE` (`test/foo_SUITE.erl`) | Erlang Common Test |
| PascalCase stem *prefix* `Test` (`TestFoo.pas`, `Test*.java`, `TestFoo.hx`) | Delphi, Java (Surefire), Haxe |
| PascalCase stem suffixes `Suite`, `IT`, `ITCase`, `TestCase`, `Fixture` (`FooSuite.scala`, `FooIT.java`) | Scala (munit), Java (Failsafe), C# |
| PascalCase rule limited to C#/Java/Kotlin/Swift — must extend to `.php`, `.cls`, `.groovy`, `.scala`, `.hx`, `.cfc`, `.as`, `.m`, `.mm`, `.ts`, `.hs`, `.st` | PHP, Apex, Groovy, Scala, Haxe, CFML, ActionScript, Obj-C(++), MATLAB, Haskell, Smalltalk |
| extension `.t` outside `t/`; dir `xt/` | Perl, Raku |
| extension `.test` (`tests/foo.test`) | Tcl (tcltest) |
| extension `.bats` | Bash (bats-core) |
| extension `.feature`, dirs `features/`, `features/step_definitions/` | Gherkin: Ruby, Java, JS, Python (behave), Go, .NET |
| extension `.robot`, `.resource`; dirs `atests/`, `utests/` | Robot Framework |
| extension `.plt` (PlUnit sidecar) | Prolog |
| extension `.pf` (pFUnit) | Fortran |
| extension `.lvt`, `.tlg`; dir `testfiles/` | TeX / LaTeX (l3build) |
| extension `.wast` | WebAssembly |
| stem suffix `.t` before the real extension (`Counter.t.sol`) | Solidity (Foundry) |
| stem suffix `.tftest` (`main.tftest.hcl`), `.tofutest` | Terraform / OpenTofu |
| stem suffix `.testclasses` (`zcl_foo.clas.testclasses.abap`) | ABAP |
| camelCase dir segments ending `Test`/`Tests` (`src/commonTest/`, `src/jvmTest/`, `src/androidTest/`, `src/androidUnitTest/`, `src/iosTest/`, `src/integrationTest/`, `src/testFixtures/`) | Kotlin Multiplatform, Android, Gradle |
| dir segments ending `.Tests`, `.Test`, `.UnitTests`, `.IntegrationTests`, `.Specs` (`src/Foo.Tests/`) | C#, F#, VB.NET |
| dir `molecule/` (and `molecule/*/verify.yml`) | Ansible |
| dirs `tb/`, `testbench/`, stem suffix `_tb` | Verilog, SystemVerilog, VHDL |
| dirs `e2e/`, `integration/`, `functional/`, `acceptance/`, `regression/`, `it/`, `cypress/`, `playwright/`, `smoke/` | JS/TS, HTML/CSS sites, most stacks |
| dirs `testdata/`, `fixtures/`, `__fixtures__/`, `__mocks__/`, `__snapshots__/`, `snapshots/`, `golden/`, `expected/` | Go (`testdata/` is compiler-special), JS, Idris, Chapel |
| `conftest.py` (no `test` in the stem, but load-bearing for a targeted pytest run) | Python |
| `spec_helper.rb`, `rails_helper.rb`, `test_helper.exs`, `spec/spec_helper.cr` — inside `spec/`/`test/` so caught, but only if the *directory* rule runs before the filename rule | Ruby, Elixir, Crystal |
| `testthat/` as a nested dir name (caught only via its `tests/` parent) | R |
| `*.good` expected-output companions | Chapel, Awk (`*.ok`), Idris (`expected`) |
| in-system/inline forms with no path at all: `#[cfg(test)] mod tests`, `test "..." {}`, `unittest {}`, `(module+ test ...)`, `expect`, `:- begin_tests`, `let%test`, `#guard`, `-ifdef(TEST)`, `import.meta.vitest`, doctests | Rust, Zig, D, Racket, Roc, Prolog, OCaml, Lean, Erlang, JS/TS, Python/Elixir/Julia/Haskell/Nim |
