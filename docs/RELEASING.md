# Releasing Warp Whistle

## Status

This is the approved replacement process. Do not use local packaging-and-upload steps for a GitHub release.

Releases are built and published by the manually dispatched **Release Alpha** GitHub Actions workflow.

1. Push the intended committed `main` revision.
2. In GitHub, open **Actions** and run **Release Alpha**.
3. Keep `main` selected, enter the approved alpha tag, and explicitly choose whether an existing alpha release may be replaced.
4. The workflow runs tests, creates the self-contained Windows ZIP, verifies its contents, and publishes that ZIP as the only release asset.

The workflow must fail without publishing when tests, packaging, or verification fail. It must not publish a bare executable.

It creates the package from the selected commit, verifies the ZIP before upload, then verifies that the published release has exactly that ZIP asset. The workflow is the only component that creates or moves a release tag.

For the current pre-release cycle, `v0.1.0-alpha.1` is replaced only when explicitly approved. Do not create a higher version tag unless a new version is requested.
