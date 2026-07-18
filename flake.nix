{
  description = "Visual Relay reproducible development shell";

  inputs = {
    nixpkgs.url = "github:NixOS/nixpkgs/nixpkgs-unstable";
  };

  outputs = { self, nixpkgs }:
    let
      systems = [ "aarch64-darwin" "x86_64-darwin" "x86_64-linux" "aarch64-linux" ];
      forAllSystems = nixpkgs.lib.genAttrs systems;
    in
    {
      devShells = forAllSystems (system:
        let
          pkgs = import nixpkgs { inherit system; };
          nonoPinned = pkgs.rustPlatform.buildRustPackage {
            pname = "nono";
            version = "0.66.0";

            __darwinAllowLocalNetworking = true;

            src = pkgs.fetchFromGitHub {
              owner = "nolabs-ai";
              repo = "nono";
              tag = "v0.66.0";
              hash = "sha256-8Bol6B3c0pb25FG7214e6rXSKcACeOOQAd+c+1lblV4=";
            };
            cargoHash = "sha256-WqOiB+TylLsy44ZOwdGMwdKAmhqi8OXDqsKse67GOgs=";

            doCheck = false;

            nativeBuildInputs = with pkgs; [ pkg-config ];
            buildInputs = with pkgs; [ dbus ];

            meta = with pkgs.lib; {
              description = "Secure, kernel-enforced sandbox for AI agents, MCP and LLM workloads";
              homepage = "https://github.com/nolabs-ai/nono";
              license = licenses.asl20;
              mainProgram = "nono";
              platforms = platforms.linux ++ platforms.darwin;
            };
          };
        in
        {
          default = pkgs.mkShell {
            packages = with pkgs; [
              dotnet-sdk_10
              git
              bash
              shfmt
              icu
              imagemagick
              openssl
              zlib
              nonoPinned
              uv
              python313
            ];

            DOTNET_CLI_TELEMETRY_OPTOUT = "1";
            DOTNET_NOLOGO = "1";
            DOTNET_ROOT = "${pkgs.dotnet-sdk_10}/share/dotnet";
          };
        });
    };
}
