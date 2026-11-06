# Test fixtures

Photos for the face and plate detection tests. The two face photos are public domain: works of NASA, which are not copyrighted in the United States. The plates photo was dedicated to the public domain by its author (CC0). They are Wikimedia Commons' own downscaled copies, unchanged.

| File | Picture | Source | License |
|---|---|---|---|
| `portrait.jpg` (500x625) | Scott Horowitz, official astronaut portrait, NASA photo S92-44932 (1992) | [Wikimedia Commons](https://commons.wikimedia.org/wiki/File:Scott_Horowitz_-_Official_Astronaut_Portrait.jpg) | Public domain (NASA) |
| `group.jpg` (960x768) | STS-135 official crew photo, four astronauts (2011), by Robert Markowitz for NASA | [Wikimedia Commons](https://commons.wikimedia.org/wiki/File:STS-135_Official_Crew_Photo.jpg) | Public domain (NASA) |
| `plates.jpg` (960x639) | A Renault Trafic van of the French National Police, with more police vans behind it (2012), by Jebulon | [Wikimedia Commons](https://commons.wikimedia.org/wiki/File:Car_Police_Renault.jpg) | CC0 1.0 (public domain dedication) |

The tests only check how many faces and plates are found and where. Nothing identifies anyone or reads a plate.

## Old projects

`project-v1.json` to `project-v5.json` are `project.json` files as earlier versions of ZeyoCut saved them, for the migration tests. Their assets aren't included; the tests only read the files.

| File | Format | Saved by | What's in it |
|---|---|---|---|
| `project-v1.json` | 1 | Phase 1 (the Tora Jumps demo) | a cartoon cat rig: image layers with drawings and keyframes, text and two sound effects |
| `project-v2.json` | 2 | Phase 3's smoke run, from Phase 2 tools only | three cuts of one clip with a speed change, fades and a dip to black, plus an imported variable frame rate clip. No version 2 file was kept, so this one had its version set back to 2 by hand and its `originalFile` path replaced |
| `project-v3.json` | 3 | Phase 3's smoke run | the camera, blur, mask, tint, outline, shadow and a motion path, over video and text |
| `project-v4.json` | 4 | Phase 4's smoke run | a presenter cut out of the footage with text behind them |
| `project-v5.json` | 5 | Phase 6's smoke run | two redactions following three tracks over two clips |
