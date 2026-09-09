# Jellyfin 12 episode grouping patch

Jellyfin 12 can collapse distinct episodes into alternate versions when filename
parsing guesses an episode number or several episodes share an air date. This
fork keeps these files separate. Confident, explicit season and episode names
still support multiple versions.

The source starts at the exact Jellyfin12.0 release commit
`6c073e19ddf604b2369c638716164fdab4c952dc` and includes
[upstream fix17890](https://github.com/jellyfin/jellyfin/pull/17890), commit
`30eb0d7016441c13d9bf58a1958102321ea2f740`, for
[issue17885](https://github.com/jellyfin/jellyfin/issues/17885).
The extra guard excludes **all date-only keys**: two different episodes can air
on the same day, and this filename parser cannot consult their separate NFO
episode numbers. This deliberately requires explicit season/episode filenames
for automatic version grouping, including for genuine alternate date-named
encodes. It does not rename files or edit metadata.

The image builds `Emby.Naming` from this source and replaces only that assembly
and its debugging symbols in the pinned LinuxServer12 image. Web12, the
self-contained .NET runtime, FFmpeg, GPU libraries, entrypoint, and environment
remain supplied by the unchanged base image. Build and upstream provenance are
listed in `provenance.json`; the image also carries this document, the source
license, and its ABI/runtime verification result under
`/usr/share/jellyfin-homeserver`.

## Verification and publication

The build runs all upstream Naming tests plus regressions for ambiguous absolute
numbers and same-date distinct episodes. The separate verifier compares the
candidate's assembly identity, assembly references, and public/protected API with
the exact LinuxServer assembly. It then executes both resolvers with dependencies
loaded from the unchanged LinuxServer image. It requires the original image to
reproduce the date-grouping failure, the patch to separate those files, and
explicit season/episode versions to remain grouped. No library, account, or
media data is needed for these checks.

Run the checks locally without starting Jellyfin:

```sh
docker buildx build -f deployment/homeserver/Dockerfile --target verification \
  --output type=local,dest=artifacts/verification .
```

The `Homeserver image` workflow checks pull requests without publishing. Only
pushes to the fork's reviewed deployment branch `main` can publish
`ghcr.io/tomerh2001/jellyfin:latest` and the accompanying `sha-<commit>` tag.
No workflow deploys or restarts a server. The image currently targets
`linux/amd64`, matching the supported deployment.

Existing version associations may already be stored in a Jellyfin database.
The naming fix prevents new misgrouping; recovery of existing associations must
be verified on a database clone, preserving play state and metadata, before any
production rollout. An image rebuild alone is not proof of database recovery.

When an upstream release contains both protections, compare the actual published
replacement image and its resolver behavior before retiring this overlay. A
merged PR alone does not establish that a moving image tag includes its fix.
