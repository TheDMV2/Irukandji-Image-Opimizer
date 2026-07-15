<p align="center">
  <img src="/irukandji-logo.jpg" alt="Irukandji Logo">
</p>

# Irukandji - Jellyfin Image Compressor
A Jellyfin plugin to optimize images on the fly, and in batch mode.
[WARNING: this was 100% AI coded. All text was 100% human written.]

Irukandji are one the world's smallest (species of) jellyfish, at about 1 cubic centimetre in size.

This plugin attempts to make Jellyfin smaller. Smaller metadata images. Smaller profile images. Smaller trickplay images. Small thumbnail images that are generated on the fly.

All image processing is handled by Jellyfin 10.11.11's internal Skiasharp 3.119.4 package, but should be ready to take advantage of Skiasharp 4.150.1 when Jellyfin upgrades in v12.

## What it does

### Function 1 - On-the-fly Interception
Intercepts client image requests and serves smaller, more highly compressed version. For example, the web client is hard coded to request quality 96 jpgs. This will replace that, server-side, with whatever level of compression you like (*recommended: 75 or under*). It also sets a maximum dimension of 1920 pixels on the long side, and will reduce any image larger than that down to 1920px (*configurable to any maximum size you like. Recommended: 3840px at most, but 1920px is fine.*)

Not only that, but it will save it in a better format; if a png, it will return a jpg. If a tranparent png, it will return a webp file.

If AVIF is available, it will use that format as well (Jellyfin 10.11.11 does not support AVIF, but JF 12 should)

It identifies mobile clients and offers alternate compression settings (usually higher compression and a default of a maximum of 720px on the long side).

### Function 2 - Caching

It also creates a cache in memory to store requested images, so for a busy server with multiple clients, it will only compress the images once, and serve them multiple times, further speeding responses.

### Function 3 - Saving Metadata
Intercepts metadata image downloads that Jellyfin does, and recompresses them before saving locally. When a new movie or show is scanned into Jellyfin, Irukandji will intercept the image and recompress it before it is saved locally.

### Function 4 - Image Compression
Will manually go through your images and recompress / resize them for you, based on your settings. Metadate, avatars, trickplay images (so you can just resize them instead of recreating them from scratch), and even just every image in your media folders.

Shows how much each image was compressed, in both file size and dimensions, along with a total. Plus, if a compression attempt didn't result in significant change (*5% smaller by default*) it discards the result so the image isn't recompressed, and you don't lose image quality for nothing.

It optionally makes a backup of any images compressed this way so they can be restored.

Also, you can do a dry run of any part of this function, to see how much space you might save, test for any problems, etc, and it will report all differences, but not write anything to the drive.

### Function 5 - Testing

You can either upload your own image, or search for a movie poster to run a single manual test to see how effective your settings are. Shows the results side by side.

## Prerequisits

None!

## Installation

Either drop into your plugins folder, or install the manifest and install like all other plugins.

# WARNING - AI CODED

Everything behind the scenes was coded by free-version Gemini. The project quickly got too big for free-version Claude. Would paid versions have produced better code? Maybe. This is open source, and I am open to fixing and improving it. Because the third function can delete and modify potentially important files (like maybe DVD covers, or scanned CD pamphlets), use it with caution. Back up first, test second, and only then run it for real.
