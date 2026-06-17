class VisualRelay < Formula
  desc "Avalonia desktop control room for Relay-style LLM task processing"
  homepage "https://github.com/Nicholas-Westby/visual-relay"
  license "MIT"
  version "0.1.0"

  depends_on "uv"
  depends_on "jedisct1/nono/nono"

  on_macos do
    if Hardware::CPU.arm?
      url "https://github.com/Nicholas-Westby/visual-relay/releases/download/v0.1.0/visual-relay-osx-arm64.tar.gz"
      sha256 "REPLACE_WITH_ACTUAL_SHA256_ARM64"
    else
      url "https://github.com/Nicholas-Westby/visual-relay/releases/download/v0.1.0/visual-relay-osx-x64.tar.gz"
      sha256 "REPLACE_WITH_ACTUAL_SHA256_X64"
    end
  end

  def install
    # The macOS release tarball ships app/VisualRelay.app (Info.plist + .icns)
    # alongside the bare app/VisualRelay.App payload. Install the whole tree into
    # libexec; the visual-relay launcher execs the bundle's inner binary when the
    # .app is present (so the Dock shows the brand icon) and falls back to the
    # bare exec otherwise. This stays a CLI formula: a bin symlink, no GUI wrapper.
    libexec.install Dir["*"]
    bin.install_symlink libexec/"visual-relay"
  end

  test do
    system "#{bin}/visual-relay", "launch", "--help"
  end
end
