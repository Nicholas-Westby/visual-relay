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
        in
        {
          default = pkgs.mkShell {
            packages = with pkgs; [
              dotnet-sdk_10
              git
              bash
              icu
              openssl
              zlib
              nono
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
