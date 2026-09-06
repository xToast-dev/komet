# Komet — Harmony-Performance-Patches für Vintage Story 1.22.7 (Client)

> Bis 1.51.8 hieß die Mod *VsPerf*. Seit dem Rename heißt alles **Komet** — modid `komet`,
> Befehl `.komet`, Config `ModConfig/komet.json`, Datei `Komet.dll`, C#-Namespace `Komet`,
> Messlatte `KometBaseline.dll`.
> Die Versionszählung fing dabei bei **1.0.0** neu an; jeder Build trägt zusätzlich einen
> Zeitstempel (`komet 1.0.0 (b260830.1917)` = kompiliert am 30.08.26 um 19:17), damit ein
> Feld-Log sagt, *welche* DLL da lief.

Reduziert die **CPU-Zeit im Main-Thread** des Clients. Der Client fährt Game-Tick *und*
alle Render-Stages auf dem Main-Thread — Main-Thread-Millisekunden *sind* die Framerate.

Schwerpunkt: **alles, was mit der Renderdistanz skaliert.**

Gebaut gegen `/opt/vintagestory` (VS 1.22.7, net10.0), Harmony 2.4.2.

---

## Warum bei +1000 Blöcken die FPS einbrechen

Vier Posten wachsen mit der Sichtweite, drei davon direkt im Main-Thread:

| Posten | Skalierung | Patch |
|---|---|---|
| Chunk-Mesh-Upload zur GPU | **quadratisch** (`ViewDistanceSq`), zusätzlich mal Rückstau | Bulk-Copy + adaptives Budget |
| Sichtbarkeits-Sweep über alle Mesh-Teile | linear mit dem Pool-Bestand, ~3× pro Frame | SoA-Cache (v1.0) |
| Draw-Ranges an `glMultiDrawElements` | linear mit sichtbaren Chunks | benachbarte Ranges verschmelzen |
| Occlusion-Culling-Raywalk | quadratisch mit der Schale | hoisted + Gitter + parallel |

Der mit Abstand größte davon ist der **Upload-Pfad** — Details unten. Er erklärt auch,
warum das Problem beim *Bewegen* auftritt und nicht beim Stillstehen in geladenem Gelände.

---

## Sichtbarkeits-Sweep: `MeshDataPool.FrustumCull`

**Was Vanilla macht.** Für jedes getesselierte Chunk-Mesh-Teil im Speicher — nicht nur für
sichtbare — läuft pro Frame:

```
MeshDataPool.FrustumCull
  └─ ModelDataPoolLocation.IsVisible          (switch über den Cull-Modus, pro Teil)
       └─ FrustumCulling.InFrustum*           (bis 6 Aufrufe)
            └─ Plane.AABBisOutside(Sphere)    ← kopiert 24 B Sphere + 32 B Plane by value,
                                                 3 float-Divisionen durch √3 pro Ebene
```

Das passiert **~3× pro Frame** (Opaque, Shadow Far, Shadow Near) über den *kompletten*
Pool-Bestand. Der Loop ist dabei nicht rechen-, sondern **speichergebunden**: pro Teil
hängen zwei separate Heap-Objekte (`ModelDataPoolLocation` + `Bools`) am Pointer-Chase.

**Was Komet daraus macht.** Pro Pool liegt eine Struct-of-Arrays-Kopie der Cull-Geometrie
(x, y, z, halbe Kantenlängen, LOD, Index-Range). Der heiße Loop streamt linear über 24 Byte
pro Teil; das Heap-Objekt wird nur noch für die Teile angefasst, die den Geometrietest
überleben. Dazu:

* Ebenen einmal pro Sweep in ein flaches, vorzeichen-vorberechnetes Array gehoben
* √3-Division einmal pro Teil statt einmal pro Ebene
* der LOD-`switch` (ein *unvorhersagbarer* indirekter Sprung pro Teil — das war überraschend
  der größte Einzelposten) wird zu zwei Lookup-Tabellen `distSq > lo && distSq < hi`
* die 5 bzw. 6 Ebenentests laufen branchlos (`&` statt `&&`), damit die unabhängigen
  Skalarprodukte pipelinen statt eine Kette fehlvorhergesagter Sprünge zu bilden
* optional: ganzer Pool per gecachter Bounding-Box verworfen
* `AllocatedTris` wird beim Cache-Aufbau einmal summiert statt jeden Frame neu

Der Cache wird invalidiert, wenn ein Pool Teile gewinnt oder verliert (Postfix auf `TryAdd`
und `RemoveLocation`, plus ein Count-Abgleich als Sicherheitsnetz).

**Die Arithmetik ist unverändert.** `sign*radius/√3` ist in float exakt `±(radius/√3)`, also
reproduziert die umgestellte Form dieselbe Rundung Term für Term. Auch das NaN-Verhalten
(`!(d < 0)` zählt als „innen") und der Umstand, dass `InFrustumAndRange` die Far-Plane
absichtlich auslässt, sind übernommen.

### Gemessen (`./build.sh bench`, Release, dieser Rechner)

Synthetischer Pool-Bestand, Kamera in 24 Richtungen, 5 Cull-Modi, LOD 0–3 plus
Out-of-Range-LOD, versteckte und occlusion-gecullte Teile:

```
equivalence: 1680/1680 identical          ← Byte-für-Byte gleiche Ausgabe wie Vanilla
equivalence (pool-box on): 1440/1440 identical

throughput: 24000 mesh parts               vanilla        fast   speedup
CullNormal                                 0,705ms     0,284ms     2,48x
CullInstantShadowPassNear                  0,232ms     0,111ms     2,10x
CullInstantShadowPassFar                   0,224ms     0,078ms     2,89x

per frame (drei Sweeps):  1,158 ms -> 0,470 ms   (0,687 ms gespart, 2,46x)
worst case (jeder Pool jeden Frame dirty): 0,986 ms vs 1,158 ms vanilla

scaling      parts     vanilla        fast     saved
             6.000     0,260ms     0,088ms   0,173ms
            12.000     0,557ms     0,202ms   0,355ms
            24.000     1,146ms     0,468ms   0,678ms
            48.000     2,347ms     1,047ms   1,300ms
```

Der Worst Case — jeder Pool ändert sich jeden Frame, also volle Cache-Neuaufbauten — ist
immer noch schneller als Vanilla. Ein Rückschritt ist strukturell nicht möglich.

**Größenordnung.** Bei ~24 000 Mesh-Teilen sind das ~0,7 ms pro Frame. Bei 100 FPS
(10 ms Budget) also grob **7 %**, bei viel Sichtweite mehr, weil der Posten linear mit dem
Pool-Bestand wächst und der Rest des Frames nicht. Wie viele Teile bei dir wirklich im
Speicher liegen, sagt `.komet` im Spiel.

---

## Chunk-Mesh-Upload — vermutlich der eigentliche Übeltäter

Zwei unabhängige Probleme im selben Pfad.

### 0. Befund aus dem echten Spiel: der Pfad ist in 1.22.7 tot

`ClientPlatformWindows.allowPStorage` wird im ganzen Dekompilat **nirgends zugewiesen** — der
Flag ist immer `false`, also läuft der persistente Pfad nie. Deine Messung bestätigt es:
`0 bulk copies, 342.605 fell back to glBufferSubData`. Meine ursprüngliche Hypothese war für
diese Version falsch; jeder Upload geht über `glBufferSubData`.

`BulkMeshUpload` steht deshalb per Default auf `false`. Der Code bleibt, weil es eine
Option gibt, den Pfad scharf zu schalten:

**`ExperimentalPersistentMapping`** setzt `allowPStorage = true`. Die Engine erkennt
`GL_ARB_buffer_storage` bereits selbst und hat einen vollständigen persistenten Schreibpfad —
sie benutzt ihn nur nie. Damit schreiben Uploads direkt in gemappten GPU-Speicher, statt per
`glBufferSubData` in einen Puffer, aus dem die GPU womöglich gerade liest (da kommen die
Upload-Spitzen her). Und dann greift auch der Bulk-Copy-Patch unten.

Ein Pfad, den die Entwickler nie einschalten, ist ein Pfad, den sie nie getestet haben —
darum Default aus. Wenn Terrain kaputt aussieht: wieder auf `false`.

### 1. Elementweises Kopieren in write-combined GPU-Speicher

`ClientPlatformWindows` legt Chunk-VBOs mit `GL_MAP_PERSISTENT_BIT | GL_MAP_COHERENT_BIT` an
und befüllt sie dann so (`updateVAO`, sechs Überladungen, dazu `updateIndices`):

```csharp
float* ptr = (float*)vboPtr;  ptr += offset / 4;
for (int i = 0; i < count; i++) *(ptr++) = data[i];
```

Eine skalare Store-Schleife ist die schlechteste Art, diesen Puffer zu füllen: der Speicher ist
write-combined, 4-Byte-Stores leeren die WC-Buffer ständig halbvoll statt volle Cache-Lines
auszuliefern, und wegen der Array-Bereichsprüfung vektorisiert die Schleife nicht. Komet ersetzt
sie durch eine Bulk-Kopie derselben Bytes — identische Semantik, inklusive des `glBindBuffer`,
das Vanilla vorher macht. Der nicht-persistente `glBufferSubData`-Pfad bleibt unangetastet.

```
chunk mesh upload: scalar store loop vs bulk copy (normales RAM — der konservative Fall)
  vertices/frame       bytes      scalar        bulk   speedup
         150.000        4 MB      0,58ms      0,08ms      7,0x
         600.000       18 MB      2,39ms      0,72ms      3,3x
       2.500.000       76 MB     10,10ms      5,11ms      2,0x
```

Gemessen gegen **normales, gecachtes RAM** — das ist der konservative Fall. Das echte Ziel ist
uncached write-combined Speicher, wo der Abstand deutlich größer ausfällt. Ob der Pfad bei dir
überhaupt aktiv ist (`ARB_buffer_storage`), zeigt `.komet`: Zeile `mesh upload`, Spalte
„fell back to glBufferSubData".

### 2. Das Upload-Budget wächst quadratisch — und ist eine Rückkopplung

`ChunkTesselatorManager.OnBeforeFrame`:

```csharp
int num  = game.frustumCuller.ViewDistanceSq / 48 + 350;
int num3 = num * (3 + count / (1 << ClientSettings.ChunkVerticesUploadRateLimiter));
```

und lädt dann Chunk-Meshes hoch, bis `num3` Vertices durch sind. Der Basisterm hängt am
**Quadrat** der Sichtweite: 1.715 bei 256, **49.494 bei 1536** — Faktor 29. Der zweite Term
multipliziert das nochmal mit dem Rückstau. Das ist eine Rückkopplung: großer Rückstau →
riesiges Budget → langsamer Frame → größerer Rückstau.

Komet skaliert dieses Budget mit einem Regler, der misst, wie lange die Uploads im letzten
Frame tatsächlich gedauert haben. Der Gain ist **bei 1,0 gedeckelt** — der Mod lädt nie mehr
hoch als Vanilla, er bremst nur, wenn ein Frame das Ziel reißt, und gibt sofort wieder Gas.
Chunks poppen bei extremer Sichtweite etwas später auf; die Frame-Zeit bleibt beschränkt.

Seit 1.9 regelt er **proportional statt in festen Schritten**: die Upload-Zeit ist annähernd
linear im Budget, also ist `Ziel / Ist` direkt die richtige Korrektur, und ein Ausreißer ist in
*einem* Frame ausgeregelt statt in den vieren, die wiederholte 0,75-Schritte brauchten. Ein
Regler, der der Last hinterherhinkt, verbringt seine Zeit mit Pendeln um das Ziel — und jede
Pendelbewegung ist ein Frame, der zu lang war. Dazu ein Totband knapp unter dem Ziel (sonst
hebt er das Budget beim ersten leicht zu kurzen Frame wieder an, überschwingt, kürzt, …) und
Klammern bei 0,5× und 1,25× pro Frame, damit ein einzelner Frame ohne Rückstau nicht als
„Bahn frei" durchgeht.

Ziel per `UploadBudgetTargetMs` (Default 6 ms). `.komet` zeigt den aktuellen Gain.

**Zweiter Druck-Eingang: der Frame selbst (01.09. nachts).** Unter `mesa_glthread` misst
die Upload-Uhr nur das *Aufzeichnen* der GL-Kommandos — die eigentliche Kopie zahlt der
Treiber-Thread später, dort wo seine Queue vollläuft: in opaque, im swap, im Event-Loop.
Ein Feldlog zeigte die Konsequenz: ein Acht-Ruckler-Burst beim Streaming (opaque 16–26 ms
je Frame, `draussen 22,4` als Drain), und die ganze Zeit `throttle 100 %` — der Regler
konnte die Kosten, die er begrenzen soll, schlicht nicht sehen. Deshalb bekommt er über
`FrameStats.FrameSummary` jetzt zusätzlich die Bilanz des fertigen Frames: läuft ein Frame
über `PressureFactor` (1,75×) des gleitenden Mittels, **nachdem seine GC-Pause abgezogen
ist** (ein eingefrorener Frame ist kein Upload-Druck — den kann keine Drossel verkürzen),
und waren in dem Frame Uploads unterwegs, wird der Gain proportional gekürzt. Ein
Halte-Fenster (8 Frames) verhindert, dass die billige Upload-Uhr die Kürzung im nächsten
Frame gleich wieder anhebt — sie liest ja gerade *während* des Bursts „unter Ziel".
Die pure Regel (`PressureCorrection`) ist im Verify gepinnt (Gegenproben: GC-Abzug raus,
Halte-Fenster raus, Upload-Wächter raus — alle rot). `.komet toggle uploaddruck` schaltet
live, `UploadFramePressure` (Layout 5) persistent; Report und HUD zählen
`x frame-druck gedrosselt`.

### Die Prioritäts-Queue: vanilla ohne jedes Limit — jetzt mit eigenem Budget

Die **Prioritäts-Queue** (Block-Edits, Prioritäts-Retesselationen) wird in `OnBeforeFrame`
*vor* der Budgetprüfung abgearbeitet — und zwar bei vanilla mit `while (Count > 0)`,
komplett, in einem Frame, ohne jedes Limit. Für ihren Design-Fall (ein Spieler-Edit, ein
bis zwei Chunks) ist das richtig. Aber durch dieselbe Queue laufen auch Relight-Stürme
(Zeit-/Saisonwechsel, licht-backende Mods) und Prioritäts-Retesselationen; das Hitch-Log
vom 31.08. hat Frame um Frame `davon upload 10–27 ms` gebucht, im Stand, während so ein
Sturm lief — jedes Mal diese eine Schleife, die Dutzende Chunk-Meshes in einem Frame
hochlädt. Der adaptive Regler sieht davon nichts: sein Transpiler skaliert nur das Budget
der *normalen* Queue, und vanilla prüft das erst, wenn die Prioritäts-Queue schon leer ist.

`BudgetPriorityUploads` (Default an) setzt darum dieselbe Art Kappe wie das
Entity-Tesselations-Budget: pro Frame mindestens ein Chunk (ein Rückstau kann nie
verhungern) und mindestens ein volles Chunk-Mesh an Vertices (~65k — ein Spieler-Edit
erscheint also weiterhin im selben Frame), darüber `3 × gain-skalierte Basis` wie beim
normalen Budget. Der Rest bleibt in der Queue und läuft im nächsten Frame weiter —
verschoben, nie verloren. `.komet toggle prioupload` schaltet live auf vanilla zurück;
HUD-Zeile `prio-upload` zeigt Chunks und Verteil-Ereignisse (auch bei 0, damit „korrekt
untätig" nie wie „läuft nicht" aussieht).

---

## Draw-Ranges zusammenfassen

Die Engine legt Chunks in Tesselations-Reihenfolge in die Pools, und
`ChunkTesselatorManager` sortiert diese Queue vorher nach Distanz. Der sichtbare Teil eines
Pools besteht darum aus langen zusammenhängenden Läufen im Index-Buffer — und jeder davon
wird `glMultiDrawElements` einzeln übergeben. Benachbarte Ranges zu verschmelzen zeichnet
exakt dieselben Dreiecke in derselben Reihenfolge.

```
draw ranges pro Render-Pass und Frame
 view dist  parts pooled  visible ranges  after merging  reduction
       512         2.851             713            380       1,9x
      1024         7.468           2.192          1.211       1,8x
      1536        15.283           4.424          2.541       1,7x
 1536 frag        15.283           4.424          3.067       1,4x
```

(`frag` = nach längerem Spielen, wenn Chunk-Entfernungen die Pool-Reihenfolge fragmentiert
haben.) Kostet im Cull-Sweep gemessene **0,00 ms** — der Zweig geht im Speicherzugriff unter.

### Lücken-Merging: über frustum-geclippte Teile hinweg (1.50.0)

Das obige Merging verschmilzt nur *nahtlos* benachbarte Teile. Zwischen zwei sichtbaren
Läufen liegt aber oft genau ein unsichtbares Teil — und wenn dessen Box **komplett außerhalb
des Frustums** liegt, darf die Range es einfach überspannen: die GPU clippt jedes seiner
Dreiecke vor der Rasterisierung, es entsteht kein einziges Fragment, das Bild ist identisch.
Der Tausch ist Vertex-Arbeit auf einer GPU, die laut den Feld-Reports zu ~80 % idle ist,
gegen eine Draw-Range weniger auf der CPU, wo der Frame tatsächlich verbraucht wird
(15.749 Ranges über 929 Draw-Calls bei gpu 2,5 ms von 13,4 ms Frame).

Was nie überbrückt wird, mit Grund:

* **Distanz/LOD-verworfen, aber im Frustum** — LOD 2 und LOD 3 sind derselbe Chunk in zwei
  Auflösungen; beide zu zeichnen gäbe Z-Fighting.
* **Versteckt oder occlusion-verdeckt, aber im Frustum** — die würden rasterisiert.
* **Schatten-Pass: im Licht-Frustum, aber außerhalb der Schatten-Reichweite** — das würde
  einen Schatten werfen, den Vanilla unterdrückt.
* **Freie Bytes zwischen Allokationen** — abgeräumte Indizes zeichnen Geometrie-Reste.

Der Beweis läuft pro Lücke als Kachel-Walk: jedes Zwischenteil muss die Byte-Kette exakt
fortsetzen *und* seinen eigenen Box-Test gegen alle Ebenen verlieren; schließt die Kette die
Lücke nicht exakt, wird nichts verschmolzen. Die Sweep-Gegenprobe (`cullcheck`) kennt die
Regel: sie akzeptiert überbrückte Bytes nur, wenn Vanillas eigener `InFrustum` das Teil
ebenfalls verwirft — alles andere bleibt eine gemeldete Abweichung. Im Harnisch:
2304/2304 Kombinationen pixel-äquivalent bei 53.048 überbrückten Lücken.

Messbar per Stress-Phase `luecken-merge aus` und Toggle `.komet toggle gapmerge`;
der Report zeigt `luecken-merge: N ranges/frame … gespart`. Config: `GapMergeDrawRanges`.

---

## Occlusion-Culling parallelisieren

`ChunkCuller.CullInvisibleChunks` schießt drei Strahlen auf jede Position einer Schale um den
Spieler und läuft sie Chunk für Chunk ab. Die Schale wächst mit der Sichtweite — 3.878
Positionen bei 256, **24.678 bei 1536** — und jeder Schritt kostet sechs Ebenen-Schnitte plus
einen Dictionary-Lookup, alles auf einem Thread.

Drei Änderungen, keine davon ändert das Ergebnis:

* **Per-Strahl-Konstanten aus der Schleife.** Alle sechs Flächennormalen sind achsenparallel mit
  genau einer Komponente ±1, also kollabiert die Ebenengleichung zu
  `t = (pos[axis] + planeCenter[axis] - origin[axis]) / dir[axis]`; die beiden Vorzeichen kürzen
  sich in IEEE exakt. Alle beteiligten Werte sind Vielfache von ¼ und damit exakt darstellbar,
  das Umklammern der Subtraktion ist also ebenfalls exakt.
* **Flaches Gitter statt Dictionary.** Die Chunk-Map wird einmal unter demselben Lock in ein
  Array gespiegelt, das Vanilla ohnehin schon nimmt, um die Sichtbarkeits-Flags zu löschen.
  Nebeneffekt: der Walk liest nicht mehr ein Dictionary, das der Main-Thread gerade umbaut —
  ein Datenrennen, das in Vanilla existiert.
* **Parallel.** Sichtbarkeit ist eine Vereinigung über unabhängige Strahlen, also
  reihenfolge-unabhängig; das Markieren setzt ein Bit, das während des Walks nur gesetzt und
  nie gelöscht wird.

```
occlusion culling ray walk
 view dist      rays     vanilla     hoisted  +flat grid   +parallel    total
       256    11.634      27,9ms       8,8ms       8,2ms       1,3ms    21,2x
       512    21.042      35,9ms       7,1ms       5,4ms       0,9ms    41,5x
      1024    44.466      37,9ms      18,5ms      13,0ms       2,0ms    19,1x
      1536    74.034      70,4ms      35,5ms      23,7ms       3,6ms    19,5x
```

Das läuft auf dem `chunkculling`-Worker, kostet also keine Frame-Zeit direkt — aber es hörte
bei hoher Sichtweite nie auf, einen Kern zu belegen, und die Ergebnisse hinkten entsprechend
hinterher. Frische Occlusion-Daten heißt: weniger Chunks als sichtbar markiert, also weniger
Draw-Ranges und weniger GPU-Last.

---

## Schatten: der harte Cutoff in der Ferne

Die beiden Schatten-Kaskaden sind unterschiedlich konfiguriert. Die **nahe** ist in sich
stimmig — bei Schattenqualität 4 setzt sie das Uniform auf 39 und baut die Box für 39. Die
**ferne** nicht:

```csharp
game.shUniforms.ShadowRangeFar = (float)num;                                // 510
PrepareForShadowRendering((shadowMapQuality > 1) ? (num / 2.0) : num, ...); // 255
```

`shadowcoords.vsh` blendet den Schatten über zwei Terme aus: einen Distanzterm
`max(0, len/shadowRangeFar - 0.15)` und Randterme auf den Shadow-Map-UVs, **mal 10**. Das
Gewicht ist bei Summe 0,75 auf null. Mit `shadowRangeFar = 510` wäre die weiche
Distanz-Verblendung erst bei `len = 459` fertig — die Shadow-Map endet aber schon bei 255.
Also greifen zuerst die Randterme, und wegen des Faktors 10 schalten die den Schatten über
wenige Meter hart ab, statt ihn auszublenden. Das ist der sichtbare Cutoff.

`FixShadowFadeCutoff` (Default an) setzt das Uniform auf die Reichweite, die die Map
tatsächlich abdeckt. Dann ist der Distanzterm bei `0,9 × Distanz` fertig, bequem innerhalb
der Box: der Schatten blendet weich aus und die Randterme kommen nie zum Zug. Kostet nichts;
Schatten enden minimal näher, dafür ohne Kante.

`ShadowDistanceMultiplier` (Default 1,5 seit 1.20.0) streckt die ferne Kaskade. Größer heißt
Schatten weiter draußen, aber dieselbe Shadow-Map-Auflösung auf mehr Fläche — also klobiger —
und mehr Chunks im Schatten-Pass. Die HUD-Zeile `= schatten` zeigt sofort, was das kostet.

### Der zweite Cutoff: die Box ist ein Kegel entlang Welt-−Z

Mit dem Uniform-Fix blieb trotzdem eine sichtbare Kante, deren Abstand von der Blickrichtung
abhing. Grund steht in `ShadowBox`: `getCameraRotationMatrix()` gibt die **Identität** zurück
und `FORWARD = (0, 0, −1)` — die Box ist ein Sichtkegel entlang der festen Weltachse −Z,
egal wohin man schaut. Dazu `farWidth = R · min(1, FoV/90)` (≈ 0,78 R bei FoV 70) und
`farHeight = farWidth / Seitenverhältnis` (≈ 0,45 R bei 16:9). `loadOrthoModeMatrix` nutzt
davon nur `Width/Height/Length` und zentriert die Projektion auf die Kamera: abgedeckt ist
also `Breite/2` in jede Richtung — 0,39 R zur Seite, 0,22 R nach oben. Die weiche
Distanz-Verblendung ist erst bei 0,9 R fertig; überall wo die Map vorher ausgeht, schneiden
wieder die ×10-Randterme.

`SymmetricShadowBox` (Default an, seit 1.20.0) ersetzt die min/max-Werte per Postfix auf
`ShadowBox.update()` durch die Lichtraum-Hülle eines Würfels `[−R, R]³` um die Kamera
(dieselbe Ecken-Transformation, die Vanilla benutzt; `maxZ + ShadowBoxZExtend` bleibt).
Damit liegt die Map-Kante in **jeder** Richtung bei ≥ R und die Distanz-Verblendung gewinnt
immer — dieselbe Eigenschaft, wegen der die nahe Kaskade nie eine Naht hatte. Preis:
Texel ~1,7× gröber pro Achse; bei 6144² und Qualität 4 noch ~7 Texel pro Block. Der
Schatten-Pass-Culler nutzt weiterhin `shadowDistance` selbst, die Geometriemenge im Pass
ändert sich nicht.

---

## Schatten: die ferne Kaskade nur neu zeichnen, wenn sie sich ändert

Bei Sichtweite 1536 kosten die beiden Schatten-Pässe zusammen 15,6 ms von 36,5 ms — 43 % des
Frames dafür, das Terrain noch zweimal aus Sicht der Sonne zu zeichnen. Die ferne Kaskade ist
die Hälfte, die sich kaum ändert: sie deckt 255 Blöcke ab und die Shadow-Map bleibt über
`toShadowMapSpaceMatrixFar` in der Welt verankert, solange man sie stehen lässt.

Übersprungen wird auf **Stage-Ebene**, nicht in `SystemRenderShadowMap`. Das ist wichtig:
`OnRenderShadowFar` pusht Matrizen, die `OnRenderShadowFarDone` wieder poppt, und auf denselben
Stages hängen weitere Renderer. Das Stage-*Paar* auszulassen hält Push/Pop im Gleichgewicht,
hält alle anderen Renderer heraus und lässt das Framebuffer-Ergebnis des Vorframes stehen —
gültig, nur etwas alt.

Wann neu gezeichnet wird:

* nie öfter als alle `ShadowFarUpdateInterval` Frames — die Untergrenze für die Kosten,
* nie seltener als alle `ShadowFarMaxSkip` Frames — die Obergrenze für die Veraltung,
* dazwischen, sobald die Kamera `ShadowFarMoveThreshold` Blöcke gewandert ist oder die
  Lichtrichtung sich gedreht hat.

Die Bewegungsregel ist das, was ein festes Intervall nicht kann: **steht man still, ist die
ferne Shadow-Map bitgleich mit der, die neu gezeichnet worden wäre** — überspringen kostet dann
nichts.

**Und sie muss vor der Untergrenze geprüft werden.** Bis 1.16.0 stand `if (since < FarInterval)
return false;` *über* der Bewegungsprüfung, also wurde beim Fliegen trotzdem jeder zweite Frame
übersprungen — und das erzeugte eine sichtbare **Abrisskante beim Hoch- und Runterfliegen**. Der
Grund ist genau die Grenze der Matrix-Kompensation weiter unten: die hält eine behaltene Map
korrekt *positioniert*, aber sie kann nicht erweitern, was die Map **abdeckt**. Die Tiefentextur
enthält nur das Volumen, für das sie gezeichnet wurde; fliegt man daraus heraus, verlassen die
Sample-Koordinaten `[0, 1]`, und dort schneiden die Randterme in `shadowcoords.vsh` (mal zehn)
den Schatten über wenige Meter hart ab statt ihn auszublenden. Diese Kante springt dann bei
jedem Neuzeichnen.

Seit 1.16.1 überstimmt Bewegung deshalb die Untergrenze, und `ShadowFarMoveThreshold` liegt bei
**0,15 Blöcken**: bei 85 fps sind das etwa drei Frames Gehen, aber weniger als ein Frame
Fliegen. Wer läuft, bekommt die Ersparnis weiterhin; wer fliegt, überholt die Abdeckung nie.

**Der Teil, der zuerst fehlte und als Flackern beim Fliegen sichtbar war:** die Chunk-Shader
rechnen kamerarelativ (`truePos = vertexPosition + origin` mit `origin = poolOrigin −
cameraPos`), und `toShadowMapSpaceMatrixFar` ist für die Kameraposition des Frames gebaut, der
die Map gezeichnet hat. Bewegt sich die Kamera in einem übersprungenen Frame um Δ, landet
derselbe Weltpunkt auf einem anderen Texel — die Schatten schwimmen einen Frame mit und
schnappen beim Neuzeichnen zurück. Deshalb schreibt jeder übersprungene Frame die behaltene
Matrix als `M·T(Δ)` neu: exakt der Texel von vorher, die Schatten stehen wirklich still.
Immer vom Render-Snapshot aus gerechnet, nie inkrementell — nichts kann driften. Die
Korrektheit der Mathe (`M′·p == M·(p+Δ)`) prüft `verify` gegen eine unabhängige
Referenz-Multiplikation, gegengeprüft mit eingebautem Vorzeichenfehler.

Ist auch die nahe Kaskade gedrosselt (`ShadowNearUpdateInterval > 1`), überlässt sie jeden
Frame, in dem der ferne Pass ohnehin zeichnet, diesem und nimmt den nächsten. Ein Frame trägt
so höchstens eine Kaskade. Genau das trennt „schneller" von „flüssiger": zwei Kaskaden im
selben Frame und keine im nächsten ergibt denselben Mittelwert und sichtbares Ruckeln.

> Von 1.6 bis 1.8.1 war dieser Patch **toter Code** — die Klasse existierte, die Config-Schlüssel
> existierten, das HUD las sie, aber `KometModSystem` rief `Apply` nie auf. Verdrahtet wurde er
> in 1.8.2. `verify` prüft seitdem für *jede* Patch-Klasse, dass der Mod sie auch anfasst; der
> Test wurde gegengeprüft, indem die Verdrahtung wieder entfernt wurde — ohne sie schlägt er fehl.

---

## Was pro `FrustumCull`-Aufruf übrig blieb

Nach dem Batch-Culling aus 1.8 macht der überwiegende Teil der mehreren tausend
`FrustumCull`-Aufrufe pro Frame **gar nichts** — die Stage ist schon gecullt, der Aufruf muss
das nur feststellen. Was dieser Nichts-Pfad trotzdem kostete:

* **zwei `Stopwatch.GetTimestamp()`** (je ein `clock_gettime` über die vDSO) pro Aufruf, nur
  um eine Dauer von null zu messen. Die Uhr wird jetzt erst gelesen, wenn feststeht, dass es
  echte Arbeit gibt.
* **zwei `ConditionalWeakTable`-Lookups**, um vom Pool zu seinem Cache zu kommen — einer im
  Einstieg, einer nochmal im Kern. Jetzt einer, und davor ein direkt adressierter Memo mit
  1024 Slots: ein Array-Zugriff plus Identitätsprüfung. Der parallele Batch füllt den Memo
  beim Einsammeln, also treffen die Aufrufe danach ihn alle.

Der Memo hält seine Caches stark, die Rückreferenz auf den Pool aber schwach, und wird bei
jedem Stage-Wechsel geleert — er kann einen entladenen Pool also nicht länger als eine Stage
am Leben halten. Slots kollidieren zwangsläufig (mehr Pools als Slots); dass die
Identitätsprüfung dagegen wirklich schützt, prüft `verify` mit 1500 Pools und wurde
gegengeprüft, indem die Prüfung entfernt wurde.

Dazu geht der Batch nur noch parallel, wenn es etwas zu verteilen gibt (ab 50 000 Mesh-Teilen).
Der Thread-Pool zu bezahlen, bevor das erste Teil getestet ist, hat sich bei kleinen Pool-Sets
nicht gelohnt — im Harnisch war `CullInstant` deswegen langsamer als Vanilla.

```
in-game shape (600 pools x 640 parts = 384.000)
  1.8:  20,8 ms -> 4,98 ms  (4,2x)
  1.9:  20,8 ms -> 2,79 ms  (7,4x)
```

---

## Vier Teile pro Befehl: der Ebenentest als SIMD (1.42.0)

Der Sweep ist der größte Posten, den diese Mod im Frame anfasst (`sichtbarkeit 2,19 ms` von
9,63 ms), und sein heißester Kern ist der Ebenentest. Die Mikro-Messung sagt, wie heiß:

```
plane-test variants, 24000 parts, one sweep:
  C ref    + branchless       0,224 ms     <- was bis 1.41 lief
  E planar + AVX2 x4          0,052 ms     <- 4,3x, identische Trefferzahl
```

Zwei Änderungen, die zusammengehören:

**Geo wird planar.** Statt einem Sechs-Float-Datensatz pro Teil liegen jetzt sechs Blöcke
hintereinander: alle x, dann alle y, alle z, dann die drei Halbachsen. Vektorladungen brauchen
vier aufeinanderfolgende x in einem Register — verschachtelt ginge das nur per Gather. Und im
Kamerapass wird die Mehrheit der Teile vom LOD-Distanzband verworfen, wofür nur x und z
gelesen werden: verschachtelt zieht das den ganzen 24-Byte-Datensatz durch den Cache, planar
kostet es 8 Byte.

**Der Ebenentest rechnet vier Teile gleichzeitig** in `Vector256<double>`. Die Entscheidung ist
*bitgenau* dieselbe, nicht ungefähr dieselbe — und das sind drei getrennte Bedingungen:

* Jede Lane führt dieselben Multiplikationen und Additionen in derselben Klammerung aus.
* **Kein FMA.** Ein `MultiplyAdd` rundet einmal, wo der skalare Ausdruck zweimal rundet.
  Explizite `Avx.Multiply`/`Avx.Add` kann der JIT nicht zusammenziehen.
* **NaN zählt als „drin".** Vanillas `Plane.AABBisOutside` liefert `dist < 0`, also ist
  `!(dist < 0)` bei NaN wahr. Der naheliegende Vektorausdruck `d >= 0` ist bei NaN *falsch* und
  würde solche Teile wegwerfen. Deshalb `AndNot(CompareLessThan(d, 0), mask)`.

Die Umrechnung float→double (`CVTPS2PD`) ist immer exakt, und `ex * (±1)` ist in beiden
Breiten exakt — die Vorzeichen der Normalen dürfen also als `double` vorgehalten werden.

Gemessen an der Pool-Form, die ein echter Client annimmt:

```
in-game shape (96 pools x 1500 parts = 144.000): 7,6 ms -> 0,70 ms  (10,8x)
  of which the vector kernel: 0,96-1,02 ms skalar -> 0,69-0,71 ms mit AVX2  (~1,4x)
```

Also **rund ein Drittel vom Sweep**. Der Rest sind Zellverwerfung, der Bitmap-Scan und der
Zeiger-Sprung zu jedem überlebenden Teil — nichts davon vektorisiert.

### Die Zellgröße kippt mit: 48 → 160

Der schnellere Kernel verschiebt den Kompromiss hinter `PartsPerCellTarget`. Eine verworfene
Zelle spart jetzt viermal weniger (die Teile dahinter sind viermal billiger zu testen), während
ein längerer Bucket den Aufwand pro Bucket amortisiert und der Vierer-Schleife mehr zu tun gibt.
Gemessen an der in-game-Form, bestes aus drei verschränkten Runden, zwei unabhängige Läufe:

```
  48:0,63/0,68   96:0,61/0,64   160:0,55/0,61   240:0,64/0,66   400:0,68/0,72   800:0,85/0,89
```

Ein **inneres** Optimum, keine Randlage — deshalb ist es belastbar. Der alte Befund („96 und 160
gemessen schlechter") war für den skalaren Kernel richtig und ist schlicht nicht mehr dieselbe
Messung. Nebenwirkung fürs HUD: `zellen weg` fällt deutlich, weil es weniger und größere Zellen
gibt — das ist die Ursache, nicht ein Rückschritt.

> **Auch dieser Sweep hat zuerst sich selbst widerlegt.** Hintereinander gemessen sagte er in
> einem Lauf „160 ist das schlechteste" und im nächsten „160 ist das beste" — jeder Eintrag
> dauert Sekunden, und die Maschine driftet über den Sweep. Jetzt werden die sechs Einstellungen
> über drei Runden **verschränkt** und je auf ihrem Minimum bewertet, dieselbe Logik wie die
> Nachbar-Baselines im Stresstest. Danach reproduzieren sich beide Läufe.

> **Die Pool-Form im Harnisch war falsch.** „in-game shape" hieß bis 1.41 *600 Pools × 640
> Teile*. Die eigenen Zähler der Mod sagen etwas anderes: ~290 Sweeps pro Frame über drei
> Stages sind ~96 Pools, und ein Pool fasst bis zu 3000 Teile. Sechsmal zu viele Pools mit je
> einem Viertel der Teile schmeicheln dem Fixaufwand pro Sweep und bestrafen alles, was sich
> über einen langen Lauf amortisiert — genau das Gegenteil dessen, was hier gemessen werden
> soll. Dazu nimmt `Time()` jetzt das **Minimum aus drei Läufen** statt eines einzelnen: zwei
> aufeinanderfolgende Messungen desselben Modus wichen um 50 % voneinander ab, was genügt, um
> eine Optimierung in beide Richtungen zu „beweisen".

Beide Kernel bleiben im Build und beide laufen den vollen Äquivalenztest gegen Vanilla
(3120 Prüfungen). `verify` prüft zusätzlich die zwei Stellen, an denen „dieselbe Arithmetik"
leicht danebengeht: den **skalaren Rest** eines Buckets, dessen Länge kein Vielfaches von vier
ist, und **NaN**. Gegengeprüft mit drei Mutationen — `d >= 0` statt `!(d < 0)`, Rest-Schleife
weggelassen, Klammerung im Skalarprodukt vertauscht — alle drei schlagen fehl.

> **Der Test hat zuerst sich selbst gefunden.** In `verify` ist `MeshDataPool.FrustumCull`
> bereits gepatcht, der „Vanilla"-Pool lief also durch FastCuller: der Test verglich die Mod
> mit sich selbst und ließ die NaN-Mutation durch. `FastCuller.Enabled = false` um den
> Referenzaufruf — dasselbe, was Safemode tut — behebt das.

`.komet toggle simd` schaltet live um, das HUD zeigt den laufenden Kernel neben
`sichtbarkeit`, und die Stress-Phase `sweep-vektorkernel aus` misst ihn im Spiel.

---

## Cull-Worker: fertig ist die Arbeit, nicht die Belegschaft (01.09.)

Die dedizierten Cull-Threads (seit 1.45 statt des ThreadPools) hatten noch ein Loch, das
der Feldreport vom 01.09. benannt hat: Sweep-Ruckler mit `(davon 9,7–11,0 warten auf
threads)` **ohne** GC-Pause. Der Render-Thread wartete nicht auf Arbeit, sondern darauf,
dass der **letzte Helfer aufwacht und sich abmeldet** — auch wenn längst nichts mehr zu
tun war. Auf einer Maschine, auf der Occlusion-Walk, Worldgen-Threads und GC um sechs
Kerne kämpfen, kann genau dieser eine Weckruf viele Millisekunden im Scheduler hängen.

Zwei Änderungen: **Completion zählt jetzt Arbeit, nicht Worker** — jede Slice zählt ihre
Items, der Aufrufer kehrt zurück, sobald alle Items gelaufen sind; ein Helfer, der nie
eine Slice beansprucht hat, hält nichts, worauf irgendwer warten müsste. Der
Check-in-Zähler existiert weiter, bewacht aber nur noch den **Setup des nächsten
Batches** (die Batch-Felder dürfen nicht unter einem noch lesenden Nachzügler
umgeschrieben werden). Steht der Nachzügler dann immer noch aus, läuft der Batch **inline
auf dem Aufrufer** — ein begrenzter, selbstheilender Preis statt eines unbegrenzten
Wartens; der Report weist das als `x inline wegen kontention` aus. Die
Fehler-Semantik ist auf allen Pfaden gleich (Exception eines Work-Items kommt als
`InvalidOperationException` beim Aufrufer an, egal ob parallel oder inline), und ein
Batch, in dem alle Beteiligten werfen, gibt verbleibende Slices explizit auf, statt den
Aufrufer auf nie gezählte Items warten zu lassen.

## Was die Mod an sich selbst misst — und was das kostet (1.42.0)

Die Frage stand lange offen: **Safemode fühlt sich schneller an als der Normalbetrieb**, aber
jede Stress-Phase für ein zeichnendes System kam als Rauschen zurück, `alles vanilla
(= safemode)` eingeschlossen (+0,10 ±0,15 ms).

Der Grund ist strukturell: **Safemode schaltet ab, was komet *zeichnet* — nicht, was komet
*misst*.** Und gemessen wurde per Default eine Menge:

| Diagnose | Kosten | war Default |
|---|---|---|
| `ProfileRenderers` | jeder registrierte Renderer in einen Timing-Dekorator gewickelt | **an** |
| `SampleRetessSources` | jede 8. Dirty-Markierung mit Stack-Aufnahme | **an** |
| `VerifyCullSweepEvery` | jeder 512. Sweep zusätzlich als Vanilla-Sweep nachgerechnet | **an** (512) |

Der Renderer-Profiler war der teuerste, und der Kommentar an ihm war um zwei
Größenordnungen falsch: „rund fünfzig Renderer pro Frame". Bei Sichtweite 1536 hält der Client
**rund zehntausend** Renderer-Instanzen, fast alles Block-Entities. Das sind zehntausend
zusätzliche Interface-Dispatches und Cache-Misses in *jedem* Frame, dazu zwei
Stopwatch-Lesungen je Renderer auf dem gemessenen Viertel, dazu ein linearer Scan der
Renderer-Liste jedes Mal, wenn eine entladende Block-Entity sich abmeldet. Nichts davon
zeichnet etwas — und es verlängert genau den Frame, über den es berichtet.

Alle drei stehen jetzt auf **aus**. Sie sind weiterhin die schärfsten Werkzeuge hier (der
Profiler hat die Feuerstelle gefunden), also gehen sie live an: `.komet toggle profiler`,
`toggle retess`, `toggle cullcheck` — und jede hat eine eigene Stress-Phase, damit die
Behauptung „das kostet" gemessen statt behauptet wird.

Dazu drei Reparaturen, damit sie auch eingeschaltet weniger kosten:

* Der Timing-Dekorator hält seinen Zähler-Eimer jetzt als Feld statt ihn pro gemessenem Frame
  über den Profiling-Namen im Dictionary zu suchen — hunderte Feuerstellen teilen sich einen
  Namen, das waren tausende String-Hashes pro Frame für einen Wert, der ohnehin über Dutzende
  Frames geglättet wird.
* Die Stack-Aufnahme löst Frames einzeln auf und hört beim ersten brauchbaren auf. `GetMethod()`
  ist die teure Hälfte einer Aufnahme, und die Antwort steht fast immer zwei bis drei Frames
  weiter oben — vorher wurden dreißig aufgelöst, um einen zu benutzen.
* Der Unregister-Fix scannt nur noch, solange wirklich Wrapper existieren (`StatWrapped > 0`),
  nicht abhängig vom Enabled-Flag. Zwischen „Profiler aus" und „alles entwickelt" liegen zwei
  Schritte, und eine Block-Entity, die sich dazwischen abmeldet, muss ihren Dekorator noch
  finden, sonst bleibt ein Geist zurück. `verify` prüft genau dieses Fenster.

`.komet` und das HUD schreiben jetzt eine Zeile **`DIAGNOSE LAEUFT MIT: …`**, sobald etwas
davon an ist. Instrumentierung, die im Bericht unsichtbar ist, wird einem anderen System
angelastet — das ist genau passiert.

---

## Die symmetrische Schattenbox WAR die Safemode-Lücke (1.42.1)

Drei Mal hat der Benutzer berichtet, Safemode laufe 5-10 % schneller. Zwei Mal kam die Messung
als Rauschen zurück. Beim dritten Lauf, in einer eingeschwungenen Szene bei 6,58 ms, nicht mehr:

```
schattenbox aus (vanilla-kegel): delta -0,72 ms (+-0,08) [swap -0,21, schatten -0,32]
alles vanilla   (= safemode):    delta -0,61 ms (+-0,07) [swap -1,44, schatten +0,53]
```

Enge Fehlerbalken, und die beiden Zahlen decken sich: **die symmetrische Schattenbox ist die
Lücke, praktisch vollständig.** Rund 11 % des Frames. Sie steht deshalb seit 1.42.1 auf **aus**.

Warum sie kostet: die Box ist breiter als vanillas, also überleben mehr Mesh-Teile den
Schatten-Frustumtest — das kostet CPU-Submission in den beiden Schatten-Stages (`schatten -0,32`)
*und* Füllrate auf der GPU (`swap -0,21`). Nachgemessen statt geschätzt, vanillas Lichtraum-Box
bei R = 255:

| Sonnenstand | vanilla | Kugelbox | Faktor |
|---|---|---|---|
| 5° | 257 Blöcke | 488 | 1,90× |
| 30° | 392 | 488 | 1,46× |
| 45–65° | ~450 | 488 | 1,42× |
| 90° (Zenit) | 397 | 488 | 1,52× |

Was man ohne sie verliert: vanillas Box ist die AABB des Sichtfrustums — gebaut mit
`getCameraRotationMatrix()`, das die **Identität** zurückgibt. Sie zeigt also immer entlang
Welt-−Z, egal wohin man schaut. In den nicht abgedeckten Richtungen schneiden die UV-Randterme
in `shadowcoords.vsh` (mal zehn) den Schatten ab, statt ihn auszublenden. `FixShadowFadeCutoff`
nimmt davon das meiste weg — es hört auf, dem Shader die doppelte Reichweite zu melden — aber
nicht alles. `.komet toggle shadowbox` schaltet die Kugel live an; die Stress-Phase heißt jetzt
`schattenbox kugel an` und liest den Delta als **Kosten**.

### Die Box war außerdem 8 % zu groß — abgeleitet aus dem Shader

`shadowcoords.vsh` gewichtet die ferne Kaskade mit `clamp(1.5 - 2d, 0, 1)`, wobei
`d = clamp(uvRand*10 + max(0, len/shadowRangeFar - 0.15), 0, 1)`. Voll ausgeblendet ist der
Schatten bei `d = 0.75`, also bei **`len = 0.90 · R`** — jenseits davon ist nichts mehr
beschattet, was auch immer in der Karte steht. Und die UV-Randterme sind nur innerhalb
`uv ∈ [0.03, 0.97]` null, also in den mittleren **94 %** jeder Achse.

Daraus folgt die Halbgröße direkt: `0.94 · halfSize ≥ 0.90 · R`, also `halfSize = 0.957 · R`.
Die Box benutzte bis dahin `R` — 4,3 % zu groß pro Achse, 8 % verschwendete Lichtraum-Fläche,
bei identischem sichtbaren Ergebnis. Der Test prüft jetzt genau diese Eigenschaft gegen die zwei
Shader-Konstanten statt gegen einen erinnerten Radius, und zwar **von beiden Seiten**:
gegengeprüft mit `BoxRadiusFactor = 1.0` (Tightness schlägt an) und `= 0.90` (Deckung schlägt an,
„landet bei uv 0,973, wo die Randterme schon schneiden").

### `ShadowMapExtraQuality` zurück auf 1

Die zwei Stufen (8192, 537 MB) waren ausschließlich dafür da, die 1,5×-Verbreiterung der
Kugelbox zu bezahlen. Die ist jetzt aus, also fällt der Grund weg — und derselbe Stresslauf
zeigt, dass es ohnehin die falsche Richtung gewesen wäre (siehe unten: die Szene ist
GPU-limitiert, und Schattenauflösung ist reine Füllrate). Mit vanillas Kegel (~450 Blöcke Spanne)
liefert eine Stufe (7168) rund 16 Texel je Block gegen vanillas 13,7 bei 6144 — also
**1,17× schärfer als Vanilla** für 411 statt 288 MB.

---

## Die Szene ist GPU-limitiert — was das für CPU-Arbeit bedeutet (1.42.1)

Die aufschlussreichste Zeile des Stresslaufs ist die mit dem Delta null:

```
sweep-vektorkernel aus (skalar): delta -0,01 ms (+-0,03) [swap -1,45, schatten +0,88]
```

Der Vektorkernel nimmt real **0,88 ms von den beiden Schatten-Stages** — genau das, wofür er
gebaut wurde, und in derselben Größenordnung wie im Harnisch. Und die Zeit taucht **vollständig
im Swap wieder auf**. Bei 152 fps in einer eingeschwungenen Szene ist der Mainthread nicht die
Wand; er wartet am Buffer-Swap auf Treiber und GPU.

Daraus folgen zwei Dinge, die man nicht verwechseln darf:

* **CPU-Optimierungen sind hier gratis, aber auch wirkungslos.** Der Sweep ist nicht mehr der
  Engpass dieser Szene (`sweep aus` misst +0,38 ±0,50 — Rauschen). Der Vektorkernel zahlt sich
  dort aus, wo der Frame CPU-gebunden ist: beim Streaming, beim Fliegen, bei höherer Sichtweite,
  auf schwächeren CPUs. Er bleibt an, er kostet nichts, und er ist bitgenau.
* **GPU-Arbeit ist teuer.** Deshalb kostet die Schattenbox 0,72 ms und deshalb ist eine höhere
  Schattenauflösung hier keine gute Idee.

Die Zeile `= außerhalb ... davon swap` im HUD und die `[swap …]`-Spalte im Stresstest sind
genau dafür da. Ein Delta von null mit großen gegenläufigen Anteilen ist kein „bringt nichts",
sondern „die Arbeit wurde verschoben, nicht eingespart" — und sagt, wo die Wand steht.

---

## Chunk-Laden: den einen Tesselation-Thread arbeiten lassen

Die Warteschlange im HUD (`warteschl. 1585/5`) sagt, wo das Laden hängt: 1585 Chunks warten
auf die **Tesselation**, nur 5 auf den Upload. Der Client vermascht Chunks auf genau einem
Thread (`tesselateterrain`; `TextureAtlasManager` prüft dessen Thread-Id — mehr Threads sind
als Harmony-Mod nicht drin). Drei Dinge halten diesen Thread von der Arbeit ab:

1. **`ClientThread.Process` schläft 5 ms nach jedem Tick**, sofern kein System ein negatives
   Tick-Intervall meldet — und `ChunkTesselatorManager` meldet 0. Bei 1500 wartenden Chunks
   sind diese Nickerchen reine Ladezeit. `TesselationNoIdleSleep` meldet per Postfix −1,
   *solange* die Queue gefüllt ist; leere Queue schläft wie Vanilla.
2. **Normale Thread-Priorität** gegen den Render-Thread, unsere Cull-Worker und sieben weitere
   Worker. `TesselationThreadPriority` hebt den Thread auf AboveNormal — gesetzt aus einem
   Prefix, der auf dem Thread selbst läuft, kein Gefummel an der Thread-Liste.
3. **Jede Tesselation entpackt bis zu 27 Nachbar-Chunks** (`BuildExtendedChunkData` ruft
   `Unpack()` auf alle) — Dekompression auf dem kritischen Pfad, obwohl jeder andere Kern das
   vorher hätte tun können. `TesselationNeighbourPrefetch` startet einen Worker (BelowNormal,
   damit er nie mit dem Tesselator selbst konkurriert), der die vordersten 12 Queue-Einträge
   kopiert und deren Nachbarschaft vorentpackt. `Unpack()` ist idempotent und läuft unter dem
   `packUnpackLock` des Chunks — der engine-eigene `compresschunks`-Thread macht genau diese
   Art nebenläufigen Zugriff schon immer. Ein zwischendurch wieder gepackter Chunk wird vom
   Tesselator eben nochmal entpackt; falsch liegen kostet nur Arbeit, die ohnehin anfiel.

Ob und wie viel das bringt, zeigt die neue HUD-Zeile in der `welt`-Sektion:

```
tesselation      118/s    4,10 ms  je chunk, 1,2 nachbarn
```

ms/chunk mal Queue-Länge ist die verbleibende Ladezeit; sinkt der `nachbarn`-Anteil nach dem
Einschalten des Prefetch, wirkt er. Die Zeile kommt aus `Measure/` und erscheint auch in der
Baseline — Vorher/Nachher ist damit ein Mod-Manager-Toggle.

**Der Teardown-NRE und warum die erste Reparatur nicht hielt (01.09. abends).** Ein heiß
gehaltener Tesselations-Thread stirbt beim Weltverlassen gern mitten in
`BuildExtendedChunkData`, wenn die Chunk-Daten unter ihm weggerissen werden (die Engine
loggt das als „unclean exit"). Der erste Fix — ein `ShuttingDown`-Flag, gesetzt in
`ModSystem.Dispose`, geprüft am Tick-Anfang — schlug im Feld fehl, aus zwei Gründen, die
beide in `ClientMain.DestroyGameSession` stehen: (1) Die Reihenfolge ist
`TriggerLeaveWorld()` → `threadsShouldExit = true` → **200 ms warten** → … → `Dispose()`.
Ein Flag aus Dispose kommt also grundsätzlich **nach** dem Exit-Fenster. (2) **Ein**
Tesselations-Tick entleert die komplette Dirty-Queue — bei 11 000 wartenden Chunks läuft
der laufende Tick sekundenlang weiter, ein Guard an der Tick-Grenze greift nie. Jetzt
setzt das `LeaveWorld`-Event (feuert *vor* dem Fenster) das Flag, und ein Prefix auf
`TesselateChunk` (Priority.High, von verify gepinnt) bricht **pro Chunk** ab: der
laufende Tick läuft als No-op-Kette leer und der Thread beendet sich sauber im Fenster.
Der Mess-Postfix ignoriert die übersprungenen Chunks über ihr Null-Ergebnis von selbst.

Darunter steht jetzt auch die **Empfangsrate**:

```
empfangen         74/s   vom server
```

Das trennt die zwei grundverschiedenen Fälle: **volle Warteschlange** = der Client (die
Tesselation) ist der Engpass, die Hebel oben wirken. **Leere Warteschlange und niedrige
Empfangsrate** = der *Server* liefert nicht schneller — Worldgen oder Senden — und kein
Client-Tuning der Welt ändert daran etwas.

### Die Server-Seite (Singleplayer!) — seit 1.9.1 komplett in der Mod

In Singleplayer läuft der Server im selben Prozess und hat eigene Drosseln (die `MagicNum`-
Statics, per Datei `servermagicnumbers.json` konfiguriert — die Datei bleibt jetzt
**unangetastet**). Ein serverseitiges ModSystem (`KometServerModSystem`, die Mod ist dafür
`Side=Universal`) setzt die Werte bei jedem Weltstart im Speicher; Mod entfernen = Vanilla,
ohne Aufräumen.

| komet.json-Key | Default | wirkt auf | |
|---|---|---|---|
| `ServerWorldgenThreads` | `4` | `MagicNum.MaxWorldgenThreads` (Vanilla 1) | **maßgeblich**: wird bei jedem Weltstart angewandt, der Wert aus `servermagicnumbers.json` wird ignoriert (geklemmt auf 1–6, `1` = Vanilla-Verhalten). Bewusst nicht das Maximum: mit 6 flutete der Server 3000+ Chunks/s in den Client und drückte dessen Tesselation per Lock-Kontention von ~400 auf ~53 Chunks/s |
| `ServerRequestQueueSize` | `4000` | `RequestChunkColumnsQueueSize` (Vanilla 2000) | wird nur erhöht, nie gesenkt; die Engine loggt bei 1536er Sichtweite selbst einen Überlauf-Hinweis auf genau diesen Wert |
| `ServerChunksColumnsPerTick` | `0` = Vanilla | `ChunksColumnsToRequestPerTick` | Lieferrate war nie der Engpass; 0 lässt Vanilla in Ruhe |

Timing-Detail, warum das funktioniert: `ServerMain.Launch` konstruiert den `ChunkServerThread`
(der seine Zusatz-Thread-Zahl aus `MagicNum` ableitet) **vor** dem Mod-Load — aber die
Worldgen-Threads selbst starten erst bei `GameReady`, **nach** dem Mod-Load. Das ModSystem
setzt daher die Statics *und* korrigiert das bereits berechnete Feld
`additionalWorldGenThreadsCount` direkt.

---

## Cache-Rebuilds: nur die neuen Teile statt des ganzen Pools

Gemessen bei laufendem Nachladen: `sichtbarkeit 1,76 ms, davon rebuild 1,15 ms` — **65 % des
Sweeps und 13 % des Frames gingen für neun Cache-Rebuilds pro Frame drauf.**

Ein Rebuild kostet einen Cache-Miss *pro Teil im Pool*: er muss jedes
`ModelDataPoolLocation`-Objekt erneut lesen, und die liegen einzeln auf dem Heap. Bei
dreitausend Teilen sind das gemessen ~128 µs — für meist eine Handvoll neuer Teile.

`MeshDataPool.TryAdd` hängt neue Teile normalerweise **hinten an**; nur oberhalb von 3 %
Fragmentierung quetscht `TrySqueezeInbetween` sie in eine Lücke in der Mitte, was jeden
folgenden Index verschiebt. Die beiden Fälle zu unterscheiden ist eine einzige Frage: Ist das
neue Teil das letzte der Liste? Falls ja, bleibt der räumliche Index für alles Bestehende
gültig, und das neue Teil wird einfach *neben* dem Gitter geführt — der Sweep läuft nach der
Zellschleife noch linear über diese wenigen Überhang-Teile. Ein voller Rebuild passiert erst,
wenn der Überhang 1/16 des Pools überschreitet, oder sofort bei jedem Einschub und jedem
Entfernen.

Der `verify`-Test vergleicht über zwölf Runden und vier Cull-Modi einen Pool, der den
Anhäng-Pfad benutzt, gegen einen, der jedes Mal komplett neu baut: gleiche Dreieckszahl,
gleiche Draw-Ranges, in gleicher Reihenfolge — und er belegt, dass der schnelle Pfad wirklich
genommen wurde (null Rebuilds). Gegengeprüft, indem die Überhang-Teile versuchsweise gar nicht
gesweept wurden: dann meldet er `CullNormal: 2900 triangles vs 3200`, also genau das Bild
fehlender Chunks.

### Entfernen ohne Rebuild, Einschub trotz offener Appends (01.09.)

Der Einschub in die Mitte ist seit 1.30.0 inkrementell, das **Entfernen** war es nicht: jedes
`RemoveLocation` setzte `Dirty`, und der nächste Sweep baute den ganzen Pool neu — ein
Cache-Miss pro `ModelDataPoolLocation` plus einer pro `FrustumCullSphere`, für ein paar
Teile, die weg sind. Und Entfernen passiert **ständig**: jeder neu tesselierte Chunk, der
schon Geometrie hatte (Rand-Reparatur, Relight, Blockänderung — bei laufendem Nachladen
praktisch jeder), nimmt erst seine alten Teile aus vier bis acht Pools und hängt die neuen an;
ein Schritt über die Chunkgrenze beim Gehen lädt einen Ring aus hunderten Chunks aus und
trifft damit fast jeden Pool auf einmal. Der Feldreport vom 01.09. zeigte den Rest davon:
`0,28 ms cache-rebuild (3/frame)` im Mittel, und in Ruckler-Zeilen `davon sweep 11,7`,
`16,2` — bei einem Sweep-Mittel von 2,3 ms und ohne GC-Pause.

`FastCuller.NoteRemoved` ist das Spiegelbild von `NoteInserted`. Weil `List.Remove` nach
Referenz arbeitet, ist der Index nur *vor* dem Entfernen bekannt: ein Prefix auf
`RemoveLocation` merkt ihn sich (`__state`), der Postfix — den Harmony auslässt, wenn das
Original wirft, also nichts entfernt wurde — schließt den Slot. Im Gitter: die
zellgeordneten Arrays (sechs Geo-Blöcke, Lod, Orig) rücken ab der Position um eins auf, jede
Bucket-Grenze dahinter sinkt um eins, den Sentinel eingeschlossen. Im Überhang: der letzte
Eintrag springt in die Lücke (die Liste ist per Konstruktion ungeordnet). Danach rücken
Meta/Locs in Originalreihenfolge auf, jeder Index über dem entfernten sinkt um eins — ein
sequentieller Lauf über flache Ints, kein Location-Objekt wird angefasst — und der
verwaiste `Locs`-Slot wird genullt, damit die Engine-Referenz nicht am Cache hängen bleibt.
Pool- und Zellboxen werden **nicht** geschrumpft: eine Box, die die Teile plus ein
verschwundenes umschließt, ist immer noch eine Schranke, nur eine lockerere; der nächste
volle Rebuild (den jede Abweichung weiterhin erzwingt) zieht sie wieder fest.

Zweiter Teil derselben Änderung: **beide** inkrementellen Pfade tolerieren jetzt offene
Appends. Die Uploads landen in der Before-Stage, Einschübe und Entfernungen laufen am
Anfang der Opaque-Stage, und der erste Sweep, der die Appends einfalten könnte, kommt erst
danach — beim Streamen war also *immer* ein Append offen, und `NoteInserted` fiel deshalb
fast immer auf den Rebuild zurück, den es vermeiden sollte. Offene Appends sitzen aber per
Konstruktion **am Ende** der Liste: ein Einschub oder eine Entfernung davor verschiebt sie um
genau eins, und `Extend` (das ab `c.Count` liest) findet weiterhin genau die offenen. Die
Konsistenzregel lautet jetzt „Listenlänge ≥ Cache-Länge ± 1 mit offenen Appends, sonst
exakt gleich"; alles andere fällt nach wie vor auf den Rebuild.

Verify: der Fuzz-Test (3000 Zufallsschritte gegen einen Pool, der immer neu baut) nimmt beim
Entfernen jetzt den inkrementellen Pfad und lässt jeden dritten Sweep aus, damit Appends
über den nächsten Einschub oder das nächste Entfernen hinweg offen bleiben; er verlangt
≥ 100 inkrementelle Entfernungen (der Nachweis, dass der Pfad überhaupt läuft). Dazu ein
ausbuchstabierter Test: erstes, letztes, mittleres Teil aus dem Gitter, eines aus dem
Überhang, eines vor offenen Appends, ein eingeschobenes (Überhang mit Original-Index),
ein noch nicht eingefaltetes, und der Pool komplett leer — jede Stufe in allen fünf
Cull-Modi gegen die Referenz. Report-Zeile `inkrementell: N einfuegungen, M entfernungen
ohne rebuild`, HUD `davon rebuild … N +/M - inkrementell`. Und weil die 11-16-ms-Sweeps
im Feld nicht attribuiert waren: jede Ruckler-Zeile trägt jetzt `(davon X rebuild, N pools)`,
wenn der Rebuild-Anteil den Sweep erklärt — der nächste Log sagt, ob diese Spitzen damit
weg sind oder etwas anderes waren.

---

## Zwei Quads für 1,86 ms: die Occlusion-Query der Sonne

Der Per-Renderer-Profiler warf `Opaque-resm 1,86 ms` aus — das ist `SystemRenderSunMoon`,
also Sonne und Mond. Zwei texturierte Quads können keine 1,86 ms CPU-Arbeit sein, und die
Erklärung steht im Code:

`SystemRenderSunMoon` registriert sich **zweimal** in der Opaque-Stage. Die zweite Registrierung
läuft mit Render-Order 999 ganz zum Schluss und zeichnet die Sonne mit
`glColorMask(false, false, false, false)` — sie schreibt **kein einziges Pixel**. Ihr einziger
Zweck ist eine Occlusion-Query, die misst, wie weit die Sonne verdeckt ist; daraus wird
`SunSpecularIntensity`, das Blenden.

Teuer ist nicht das Quad, sondern **`glGetQueryObject`, zweimal pro Frame**. Mit aktivem
`mesa_glthread` — dem radeonsi-Default — muss jeder GL-Aufruf, der einen Wert *zurückgibt*, die
Befehlswarteschlange leeren und auf den Treiber-Thread warten. Exakt derselbe harte Sync, den
diese Mod beim per-Frame-`glGetError` schon zum Abschalten anbietet.

`SunOcclusionQueryInterval` (Default 4) lässt die Query nur noch jeden vierten Frame laufen.
Zwei Eigenschaften machen das zu einem Gratis-Gewinn statt zu einem Kompromiss:

* Der Pass schreibt keine Farbe — ihn zu überspringen kann kein Pixel verändern.
* Das Ergebnis geht durch `SunSpecularIntensity + (ziel − aktuell) · dt · 20`, eine zeitliche
  Glättung, die ohnehin rund 50 ms nachläuft.

`BeginQuery` und `EndQuery` liegen im selben Aufruf, ein übersprungener Frame lässt also nichts
halb offen — die Query bekommt sogar *mehr* Zeit, was die Verfügbarkeitsprüfung wahrscheinlicher
sofort beantwortet.

**Was 1.16.0 dabei übersehen hat — und was den Himmel flackern ließ:** Der Pass läuft mit
Render-Order 999 als *letzter* Opaque-Renderer, und die OIT-Stage (Himmel, Wolken, Transparenz)
beginnt direkt danach **ohne State-Reset**. Vanilla betritt OIT also immer mit dem GL-Zustand,
den dieser Pass hinterlässt: DepthTest an, **Blend an, CullFace aus**, Masken wiederhergestellt.
Die erste Fassung der Drosselung übersprang die ganze Methode — auf drei von vier Frames erbte
der Himmel stattdessen den Zustand des vorletzten Renderers, und flackerte im Vier-Frame-Takt.
Ein versteckter Vertrag, der jetzt explizit eingehalten wird: ein übersprungener Frame setzt
die vier End-Zustände trotzdem (reine State-Calls ohne Rückgabewert, also ohne Treiber-Sync).
Ist keine Platform greifbar, wird **nicht** übersprungen — ein Skip, dessen Zustand sich nicht
wiederherstellen lässt, ist genau der Fehler.

`verify` prüft den neuen Vertrag (ohne Platform nie überspringen, Intervall 1 exakt Vanilla) —
gegengeprüft mit der 1.16.0-Fassung: dann meldet er `skipped 300 frames with no platform to
restore the state`.

---

## Schatten, die beim Gehen wandern: Texel-Snapping

Die Schatten kosten nach dem Throttling nur noch ~1 ms — was übrig bleibt, ist ein
**Qualitäts**-Problem, und dafür lohnt ein Blick in den Aufbau.

Erst die Zahl, die eine naheliegende Vermutung ausräumt: die Shadow-Map ist bei Qualität 4
**6144×6144** (`max(4, quality+2) * 1024` in `ClientPlatformWindows`). Bei einer fernen Kaskade
über 255 Blöcke sind das rund **12 Texel pro Block**. Zu grob aufgelöst ist da nichts.

Das eigentliche Problem steht in `ShadowBox`:

* `getCameraRotationMatrix()` liefert die **Identitätsmatrix** — die Box folgt gar nicht deiner
  Blickrichtung, sie ist eine feste Form, an der Kamera verankert.
* `loadOrthoModeMatrix` setzt ausschließlich die drei Skalierungsterme. **Eine Translation gibt
  es nicht**, die Spalte bleibt null.

Zusammen heißt das: die Shadow-Map ist fest auf die Kamera zentriert und ihr Texel-Raster
**gleitet kontinuierlich mit dir durch die Welt**. Jeder Bruchteil eines Blocks, den du gehst,
lässt jede Schattenkante auf einer anderen Texel-Grenze neu abtasten — die Kanten flimmern und
kriechen. Das ist das klassische Shadow-Mapping-Artefakt, und es hat eine klassische Antwort:
die Projektion auf ganze Texel quantisieren, damit das Raster in der Welt stillsteht, während
du dich hindurchbewegst.

`StabiliseShadowTexels` (Default an) rechnet dazu die Kameraposition in den Lichtraum, nimmt den
Rest zur Texel-Größe und schreibt ihn als Translation in die Projektion — in eine Spalte, die
Vanilla leer lässt, es wird also nichts überschrieben. Alles Nachgelagerte (`PMatrix`,
`shadowMvpMatrix`, `toShadowMapSpaceMatrix*`) leitet sich aus derselben Matrix ab und übernimmt
den Versatz von allein. Kosten: zwei Subtraktionen pro Schattenpass.

Das Vorzeichen des Versatzes ist dabei egal — was das Kriechen beseitigt, ist *dass*
quantisiert wird; ein Vorzeichenfehler rastet auf dasselbe Gitter, nur einen halben Texel
versetzt.

`verify` prüft die Eigenschaft statt der Formel: über 400 Kamerapositionen quer durch mehrere
Texel muss der Versatz immer in `[0, Texelgröße]` liegen, die gesnappte Position auf einem
ganzen Texel sitzen, und es dürfen nur eine Handvoll verschiedener Versätze herauskommen.
Gegengeprüft mit „folgt einfach der Kamera" — dann meldet er
`offset 0,0684 outside [0, 0,0651]`.

> **Was das nicht repariert:** Der Tiefen-Bias im Shader ist konstant (`shadowCoordsFar.z -
> 0.0009`), ohne Neigungsanteil — auf schrägen Flächen gibt das entweder Acne oder abgelöste
> Schatten. Und die 3×3-PCF-Filterung ist bei 12 Texel/Block sehr scharf. Beides sind andere
> Baustellen; sag mir, welches Artefakt du siehst, bevor ich daran gehe.

---

## Der Fund, für den das Profiling gebaut wurde: `RenderRange` wird ignoriert

Erste Messung mit funktionierendem Per-Renderer-Profiling, Sichtweite 1536:

```
── teuerste renderer ──────────────────────
 Opaque-firepi             8,14 ms  opaque
 Opaque-ret-op             2,08 ms  opaque
 Before-chtema             0,88 ms  before
```

**8,14 ms von 21,7 ms Frame in einem einzigen Renderer** — `FirepitContentsRenderer`, also die
Feuerstellen. `Opaque-ret-op`, das komplette Terrain, kostet 2,08 ms. Die Feuerstellen kosten
das Vierfache des Terrains.

Der Grund steht in der Engine: `IRenderer.RenderRange` ist dokumentiert als die Entfernung,
jenseits derer ein Renderer nicht mehr aufgerufen werden muss, und die Renderer füllen ihn
gewissenhaft aus (Feuerstelle 48 Blöcke, Inventar-Item 24, Rift-Test 100). Nur liest
`ClientEventManager.TriggerRenderStage` das Feld **nie**. Jede Feuerstelle, jedes Schild, jeder
Amboss in **jedem geladenen Chunk** rendert damit in jedem Frame — bei Sichtweite 1536 sind das
zehntausende Chunks. Und `FirepitContentsRenderer.OnRenderFrame` macht pro Feuerstelle ein
komplettes `Use()`/`Stop()`-Paar des Standard-Shaders, fünfzehn Uniform-Uploads, einen
Lichtabruf aus der Welt und einen Draw-Call.

`CullDistantBlockRenderers` — **in 1.19.0 vollständig entfernt**, nicht nur deaktiviert.

> **Warum aus:** Per Laufzeit-Bisektion (`.komet toggle gate`) als Ursache von Welt-Glitches
> und Flackern identifiziert. Die genaue Mechanik wurde nie festgenagelt; der Verdacht ist
> dieselbe Klasse wie der Sonnen-Pass-Bug — übersprungene Renderer hinterlassen anderen
> GL-Zustand für die Nachfolger, und *welche* übersprungen werden, ändert sich mit jeder
> Kamerabewegung. Dazu kommt: die 8,14 ms der Feuerstellen wurden **vor** dem
> `UnregisterRenderer`-Fix gemessen — ein Großteil davon waren mutmaßlich **Geister-Renderer**,
> die es seit 1.17.3 nicht mehr gibt. Der Filter bekämpfte also teils ein Symptom, dessen
> Ursache inzwischen behoben ist. Erst wieder erwägen, wenn die `teuerste renderer`-Liste
> ohne ihn erneut einen Ausreißer zeigt.

Zwei Sicherungen (für den Fall, dass man ihn einschaltet):

* **Nur Renderer mit einem `BlockPos`-Feld** oder einem Typ auf der geprüften Positivliste
  werden überhaupt gefiltert, und diese Einschränkung ist doppelt beabsichtigt. Ein globaler System-Renderer hat gar kein
  Positionsfeld und kann deshalb nie versehentlich übersprungen werden — wichtig, denn
  `SystemRenderOITLayers` gibt `RenderRange 1` an und muss trotzdem jeden Frame laufen. Und
  Renderer, die ihre Position als `Vec3d` halten, bleiben **absichtlich** unangetastet:
  `AnimationUtil` ist so einer, gibt `RenderRange 99` an — und sein `OnRenderFrame` *zeichnet
  gar nicht*, es schreibt den Animationszustand fort und feuert den Rückruf, der das Rendern
  beendet, wenn eine Animation ausläuft. Den zu überspringen würde Animationen einfrieren und
  den Zustandswechsel verschlucken. **Entfernungsfilterung ist nur für rein zeichnende
  Renderer sicher.**
* **Eine `Vec3d`-Position zählt nur für Typen auf `PureDrawTypes`** — einer Positivliste von
  Renderer-Typen, deren `OnRenderFrame` ich gelesen und als reines Zeichnen bestätigt habe.
  Bewusst eine Positiv- und keine Negativliste: ein unbekannter Typ behält Vanilla-Verhalten,
  und das ist der Fehler, mit dem man leben kann. Erster Eintrag: `AnimatableRenderer` — baut
  Modellmatrix, wechselt Shader, lädt Uniforms, zeichnet. Sonst nichts. Es registriert sich in
  **vier** Stages (Opaque, ShadowFar, ShadowNear, OIT) und deklariert Reichweite 99, ist also
  in `Opaque-animat`, `ShadowNear-an` und `ShadowFar-…` gleichzeitig vertreten.
* **Ein Boden von `BlockRendererMinRange`** (96 Blöcke) unter der deklarierten Reichweite.
  Vanilla zeichnet diese Dinge faktisch in beliebiger Entfernung, eine 24-Blöcke-Angabe
  wörtlich zu nehmen könnte also etwas verschwinden lassen, das du gewohnt bist. Bei 96 bleibt
  in der Nähe alles exakt wie bisher, und die tausenden Kopien weiter draußen fallen weg.

`verify` prüft genau diese Sicherungen: ein Renderer ohne Position läuft immer, einer mit
`Vec3d`-Position **und nicht auf der Positivliste** läuft immer, einer auf der Liste wird
gefiltert, einer innerhalb seiner Reichweite läuft, einer 4000 Blöcke
entfernt nicht, und einer 60 Blöcke entfernt läuft trotz deklarierter Reichweite 4 wegen des
Bodens. Jede dieser Zusicherungen wurde gegengeprüft — Boden entfernt, positionslose bzw.
`Vec3d`-Renderer mitgefiltert: dann schlägt der Test fehl.

### Was das gebracht hat

```
vorher   46 fps / 21,74 ms    Opaque-firepi  8,14 ms
nachher  76 fps / 13,23 ms    Opaque-firepi  nicht mehr unter den teuersten
                              entfernt weg   175 renderer
```

Die HUD-Liste zeigt jetzt acht Einträge statt fünf, dazu `= alle zusammen` — die Summe *aller*
gemessenen Renderer. Gegen die Stage-Summen gehalten sagt diese Zeile, ob die Liste den Frame
überhaupt erklärt. Genau weil sie das vorher nicht tat, wurde die Feuerstelle gefunden.

---

## AnimatableRenderer: ein Frustum-Gate für animierte Block-Entities (01.09.)

Derselbe Mechanismus wie bei der Feuerstelle, nur bei einem Renderer, der nie ins Profiling
geraten war: `AnimatableRenderer` (VintagestoryAPI) zeichnet jedes animierte Block-Entity —
Windmühlenrotoren, Pulverisierer, Blasebälge, Fruchtpressen, Türen und Falltüren während sie
schwingen, und jeden Mod-Block, der `BlockEntityAnimationUtil` benutzt. Er registriert sich
in **vier** Stages (Opaque oder OIT, ShadowFar, ShadowNear), deklariert `RenderRange 99`
(das die Engine nie liest) und macht in `OnRenderFrame` ohne jede Distanz- oder
Sichtbarkeitsprüfung: Shader wechseln, `GetLightRGBs` aus dem Chunk holen, ~15 Uniforms,
ein UBO-Update, einen Draw. Eine Windmühle dreitausend Blöcke hinter der Kamera kostet
genau so viel wie eine davor, dreimal pro Frame. Im Profiler stand das früher als
`Opaque-animatable 1,6 ms` und als 7-45-ms-Spitzen in Ruckler-Zeilen nahe der Basis.

Das Gate ist **exakt**: übersprungen wird nur, wenn die Bounding-Kugel des Meshes ganz
außerhalb des Frustums liegt, mit dem die Engine *diese* Stage rendert — Kamerafrustum in
Opaque/OIT, Lichtbox in den Schattenstages (`SystemRenderShadowMap` ruft vor deren
Renderern `CalcFrustumEquations` mit der Schattenprojektion; `SphereInFrustum` testet gegen
dieselben sechs Ebenen). Dort hätte die GPU kein Fragment erzeugt, nichts, das Bildschirm
oder Schattenkarte erreicht hätte, fällt weg. Und die GL-Zustands-Falle der alten
generischen Distanz-Gates greift hier nicht: eine **untätige** Instanz (`ShouldRender`
false — der Normalzustand jeder Tür) kehrt bei vanilla vor dem ersten GL-Aufruf zurück, kein
nachfolgender Renderer konnte sich also je auf den Zustand dieses Renderers verlassen.

Die Kugel kommt einmal aus dem Mesh, das der Konstruktor bekommt (Postfix), um den Pivot, um
den die Modellmatrix rotiert und skaliert (`Blockecke + (0.5, 0, 0.5)`), und wird für
Animationen gepolstert: jeder Keyframe bewegt Elemente durch Rotationen um Joint-Ursprünge
innerhalb der Shape und durch Offsets, und `|v'−C| ≤ |v−C| + 2|P−C|` schrankt eine Rotation
um einen beliebigen Punkt P — dreifacher Ruheradius plus zwei Blöcke deckt alles, was eine
Vanilla-Animation tut, mit viel Luft. Skalierung wird pro Frame gelesen (öffentliche,
veränderliche Felder); ein `CustomTransform` ist eine beliebige Matrix des Besitzers, solche
Instanzen werden nie gegated. Kosten pro aktiver Instanz und Stage: ein
ConditionalWeakTable-Lookup und sechs Ebenen-Skalarprodukte.

Config `CullAnimatableRenderers` (Default an), `.komet toggle animcull`, Safemode schaltet
es ab, Stress-Phase `animatable-gate aus (vanilla)`, HUD/Report `animatable-gate N von M
Aufrufen übersprungen` (gedruckt, solange das Gate scharf ist — 0 von 0 ist korrekte Ruhe,
keine Zeile wäre nicht von einem toten Prefix zu unterscheiden). Verify: die reine Regel
(sichtbar bleibt, hinter der Kamera fällt, an der Frustumkante mit größerer Skalierung
oder größerem Radius bleibt, NaN/Null-Skalierung oder -Radius geht an vanilla, Spiegelung
ist eine Skalierung), die Eigenschaft über 2000 Zufallskugeln („was übersprungen wird,
nennt vanillas `SphereInFrustum` unsichtbar"), Ruheradius/Polster am Einheitswürfel, und
dass eine Instanz ohne Bounds nie gegated wird.

## Wer verbraucht die Zeit? Profiling pro Renderer

Die Stage-Aufschlüsselung sagt „opaque kostet 3,4 ms", aber nicht *wer* sie ausgibt — in einer
Stage stecken Terrain, Entities, Partikel, Wetter und jeder Renderer anderer Mods. Jede
Vermutung, die ich allein aus Stage-Summen abgeleitet habe, musste ich hinterher korrigieren
(die Rebuild-These, die Zellgrößen-These, die glthread-These). Also wird jetzt gemessen statt
vermutet.

`ClientEventManager.renderersByStage` hält pro Stage eine `List<RenderHandler>`, deren
Einträge den `ProfilingName` tragen, den auch der engine-eigene Profiler benutzt. Jeder
Renderer wird in einen Timing-Dekorator gewickelt — zwei Stopwatch-Lesungen pro Renderer, bei
rund fünfzig pro Frame also ein paar Mikrosekunden.

> **Der erste Versuch war ein Harmony-Patch auf die Dispatch-Schleife, und er hat schweigend
> nichts getan.** Er wurde sauber angewandt, loggte `enabled: per renderer profiling` — und
> zeichnete nichts auf. Grund: `ClientMain.TriggerRenderStage` ist selbst gepatcht und wurde
> dabei früher neu kompiliert; der JIT hatte den Aufruf der Dispatch-Methode zu diesem
> Zeitpunkt bereits eingebettet, und an eine eingebettete Kopie kommt Harmony nicht heran.
> Ein Patch, der greift aber nie aufgerufen wird, sieht exakt aus wie einer, der funktioniert
> — dieselbe Falle wie beim toten Schatten-Throttling. Objekte in der Liste zu ersetzen
> braucht keinen Patch und kann nicht wegoptimiert werden. Die Sektion schreibt jetzt außerdem
> `(sammelt) N renderer` statt leer zu bleiben, damit „kaputt" nicht wie „noch keine Daten"
> aussieht.

Neue HUD-Sektion:

```
── teuerste renderer ──────────────────────
 chunk_opaque              1,84 ms  opaque
 entities                  0,41 ms  opaque
 ...
```

Jeden Renderer zu ersetzen heißt, dessen kompletten Vertrag weiterreichen zu müssen — ein
falsch durchgereichtes `RenderOrder` würde die Zeichenreihenfolge des ganzen Frames
verschieben. `verify` prüft deshalb: alle Renderer werden gewickelt, `RenderOrder` und
`RenderRange` kommen unverändert durch, zweimaliges Wickeln stapelt keine Dekoratoren, der
Wrapper ruft das Original wirklich und in Reihenfolge auf, die Zeiten landen in der richtigen
Stage, und `Unwrap` stellt exakt die Originalobjekte wieder her. Gegengeprüft mit einem
absichtlich falschen `RenderOrder`: dann meldet der Test „RenderOrder not forwarded".

### Die Before-Stage ist immer attribuiert (auch mit Profiler aus)

Der volle Profiler ist seit 1.42.0 default-aus — zehntausend Dekoratoren sind Messkosten,
die niemand dauerhaft tragen soll. Das hatte eine Lücke: die wiederkehrenden
Weltbeitritts-Bursts (60–87 ms im `before`-Bucket, kein GC, kein Renderer-Name im
Hitch-Log) blieben wochenlang unbenannt, weil man den Profiler *vor* dem Beitritt hätte
scharfschalten müssen. Dabei hält die Before-Stage nur eine Handvoll System-Renderer
(Entity-Vorbereitung `ree`, Chunk-Uploads `chtema`, den Liquid-Depth-Prepass `ret-prep`,
Kamera, Ambient — plus was fremde Mods dort registrieren).

`AttributeBeforeStage` (Default an) wickelt darum genau diese Stage immer und misst sie in
**jedem** Frame statt nur im gesampelten Viertel — ein Ruckler wartet nicht darauf,
gesampelt zu werden. Kosten: wenige Mikrosekunden. Eine Ruckler-Zeile kann damit
`renderer Before-ree 60,1 ms` sagen, auch wenn der Profiler nie an war; Namen unter 0,5 ms
werden unterdrückt, damit ein Opaque-Ruckler nicht sinnlos den größten Before-Zwerg
genannt bekommt. Der Unregister-Fix (Wrapper-Identität) gilt unverändert; damit
ausgerechnet die ent-registrierenden Block-Entities nicht die Zeche zahlen, bricht der
Scan pro Stage sofort ab, wenn dort nichts gewickelt ist. `.komet toggle beforeattr`
schaltet es ab, `toggle profiler` liefert weiterhin das volle Bild.

### Und die Stages, die bisher fehlten

Das HUD zeigte sieben von dreizehn Stages; `AfterOIT`, `AfterPostProcessing`,
`AfterFinalComposition` und `AfterBlit` wurden die ganze Zeit gemessen, aber nie angezeigt —
rund ein Fünftel des Frames war unsichtbar, und genau dort sitzen SSAO, God Rays und Color
Grading. Sie stehen jetzt als `post/compose` in einer Zeile. Dazu `= außerhalb`: die Zeit, die
zu keiner Stage und keinem Game-Tick gehört (Buffer-Swap, Treiber-Rückstau). Sie zu benennen
verhindert, dass man sie für einen Messfehler hält.

---

## Dreiecke: wo Redundanz steckt und wo nicht

Bei voll geladener Welt (41.936 Chunks, Sichtweite 1536) misst das HUD
`dreiecke 14.671.330 von 120.950.632` bei 122 fps. Die zweite Zahl ist das, was in den Pools
*liegt*, nicht was gezeichnet wird — Frustum und LOD werfen 88 % davon weg. Die erste Zahl,
14,7 Mio. Dreiecke pro Frame, ist die tatsächliche Last, und bei `opaque 3,72 ms` sind das
rund 3,9 Mrd. Dreiecke/s: **an dieser Stelle ist der Client GPU-geometrie-limitiert, nicht
CPU-limitiert.** Weitere CPU-Arbeit an den Passes bringt dort nichts.

### Wie die LOD-Stufen wirklich belegt sind

| Stufe | Inhalt | wird gezeichnet |
|---|---|---|
| 0 | Oberflächendetail (`SurfaceLayerTesselator`) | bis `lodBias × 640` (211 Blöcke) |
| 1 | normale Blöcke (`DoNotRenderAtLod2 == false`) | überall in Sichtweite |
| 2 | Detail-Mesh der Blöcke mit `DoNotRenderAtLod2` | bis `lodBiasFar × 640` (640 Blöcke) |
| 3 | deren vereinfachtes `Lod2Mesh` | **erst jenseits** 640 Blöcken |

**2 und 3 sind derselbe Block in zwei Auflösungen**, und `InFrustumAndRange` wählt per Distanz
genau eine davon. Im Kamera-Pass gibt es also keine Doppelzeichnung.

### Die Schatten-Passes zeichnen beide

`ModelDataPoolLocation.IsVisible` wendet in den Schatten-Modi **gar keine Distanzregel** an
(der ferne Pass prüft nur `LodLevel >= 1`). Die Schattenbox reicht aber höchstens ~415 Blöcke
— also komplett innerhalb der 640, ab denen LOD 3 überhaupt erst zuständig wäre. Ergebnis:
**jeder solche Block wird zweimal in die Shadow-Map gerastert**, einmal als Detail-Mesh und
einmal als vereinfachter Ersatz.

`ShadowSkipRedundantLod` lässt den Ersatz weg, sobald der ganze Pool näher als `lod2Bias`
liegt (ein Vergleich für den kompletten Sweep, über die entfernteste Ecke der Pool-Box — exakt,
nicht geschätzt). Das Detail-Mesh bleibt, also sollte der Schatten gleich oder minimal genauer
werden.

**Default aus.** Es ist eine echte Änderung an dem, was Vanilla zeichnet, und wie es aussieht
lässt sich nur in der eigenen Welt beurteilen — anschalten, Laub und Zäune im Schatten
ansehen, bei Zweifeln wieder aus. Der `verify`-Test belegt, was die Option tut: gezeichnete
Dreiecke sinken, kein Teil kommt hinzu, und **jedes weggelassene Teil ist LOD 3** —
gegengeprüft, indem versuchsweise LOD 2 weggelassen wurde: dann schlägt er fehl.

> Der Test misst absichtlich Dreiecke, nicht Draw-Ranges. Teile aus der Mitte eines
> zusammengefassten Laufs zu entfernen *spaltet* diesen Lauf, die Range-Zahl kann also steigen,
> während weniger gezeichnet wird. Die erste Fassung des Tests maß Ranges und meldete
> „nichts wurde übersprungen".

---

## Die Render-Passes: was in `opaque` und `schatten` überhaupt drinsteckt

Aus einer echten Messung (9,63 ms Frame): `opaque 2,57 | schatten 2,02 | oit 0,22 |
ortho 0,18 | done 0,26`, und quer darüber `sichtbarkeit 2,19 ms`. Der Sichtbarkeits-Sweep ist
kein *eigener* Posten — er läuft **innerhalb** von Opaque und den beiden Schatten-Stages. Was
davon übrig bleibt, ist GL-Submission für ein paar hundert `glMultiDrawElements`. Die Passes
zu optimieren heißt deshalb in der Praxis: den Sweep zu optimieren und Stages ganz auszulassen
(das macht die adaptive Schatten-Drosselung).

Drei Dinge am Sweep, alle bit-genau äquivalent (1680/1680):

**Ebenen und LOD-Grenzen pro Batch statt pro Pool.** `LoadPlanes` schrieb sechs Frustum-Ebenen
in Thread-lokalen Speicher — bei *jedem* der tausenden `FrustumCull`-Aufrufe pro Frame, obwohl
ein ganzer Batch dieselben Ebenen benutzt. Die Ebenen bewegen sich genau dann, wenn
`CalcFrustumEquations` läuft, und das zählt bereits die `FrustumGeneration`, auf die das
Batching ohnehin baut. Also: Cache-Schlüssel aus (Culler, Generation), und der Thread
konvertiert einmal pro Stage statt einmal pro Pool. Dasselbe für `BuildLodBounds` — **mit
einem eigenen Schlüssel**, denn `ClientMain.MainRenderLoop` setzt `lod0BiasSq`/`lod2BiasSq`
*nach* dem `CalcFrustumEquations`-Aufruf; die Generation deckt sie also nicht ab, die Werte
selbst gehen in den Schlüssel.

**Pools, die ganz im Frustum liegen, testen ihre Teile nicht mehr einzeln.** `Outside()` prüft
die Ecke, die am weitesten *in* Normalenrichtung liegt; kehrt man das Vorzeichen der
Halbachsen um, prüft dieselbe Formel die gegenüberliegende Ecke. Liegt auch die noch vor jeder
Ebene, ist die ganze Box drin — und jeder Frustum-Test an ihren Teilen ist tote Arbeit. Kosten:
fünf Ebenen-Auswertungen pro Sweep gegen zehntausende Teile. `>=` statt `!(< 0)`, damit ein NaN
„nicht sicher drin" heißt und in den Einzeltest fällt, nie umgekehrt.

**Was ich verworfen habe, weil es gemessen langsamer war:** derselbe Test pro *Zelle*. Er
kostet fünf Ebenen-Auswertungen je ~48 Teile, ob er trifft oder nicht, und in einer Ansicht,
die quer durch die Pools schneidet — dem Normalfall — trifft er zu selten: der Sweep verlor
5-10 %. Ebenso eine größere Zellgröße (96 und 160 Teile pro Zelle statt 48): 2,70 → 3,11 →
3,62 ms. Die 48 aus v1.7 bleiben das Optimum.

> **Ehrlichkeitshinweis zum Harnisch:** Die Ebenen-Zwischenspeicherung ist dort *nicht*
> messbar, weil `Time()` pro Iteration einen anderen Culler durchreicht und der Cache damit
> konstruktionsbedingt immer daneben liegt. Im Spiel gibt es pro Stage genau einen Culler.
> Der Gewinn ist also gerechnet (~3000 Aufrufe/Frame × ~50 Stores), nicht gemessen — deshalb
> zeigt das HUD jetzt `davon rebuild` in Millisekunden und `sweeps/frame ueber N pools`,
> damit die nächste Messung die Rechnung prüfen kann.

---

## „gpu-frame": die Zeile, die CPU-limitiert von GPU-limitiert trennt

Auslöser war ein konkreter Fall: **unter Wasser halbiert sich die Framerate** (11,3 → 25,5 ms),
und der Feuerstellen-Renderer springt von 0,62 auf 4,87 ms — bei identischen Feuerstellen auf
dem Schirm. Wenn dort die GPU die Wand ist (Unterwasser-Vollbildeffekte), sind die
Mehr-Millisekunden **Rückstau**, der sich dort ansammelt, wo die meisten GL-Aufrufe abgesetzt
werden — und keine CPU-Optimierung der Welt ändert daran etwas. Bisher war das nicht
entscheidbar: `= außerhalb` fängt nur Wartezeit am Buffer-Swap, Rückstau *innerhalb* der
Stages war unsichtbar.

`MeasureGpuTime` (Default an) spannt deshalb eine `GL_TIME_ELAPSED`-Query über jeden Frame
(Beginn in der Before-Stage, Ende in Done) und zeigt das Ergebnis als `gpu-frame` direkt unter
der Frame-Zeit — mit dem Etikett **`GPU-LIMITIERT`**, sobald die GPU-Zeit die CPU-Frame-Zeit
erreicht.

Die Auslese respektiert die teuer bezahlte Lektion der Sonnen-Query: `glGetQueryObject` gibt
einen Wert zurück, und jeder rückgabebehaftete GL-Aufruf ist unter `mesa_glthread` ein
Treiber-Sync. Deshalb läuft die Abfrage über einen **Vierer-Ring** (das gelesene Query ist
mindestens drei Frames alt und längst fertig) und nur **einmal pro Sekunde**. Ein Sync pro
Sekunde auf ein fertiges Query ist Rauschen; pro Frame war dieselbe Operation der teuerste
Renderer des Spiels.

`GL_TIME_ELAPSED`-Queries dürfen nicht verschachtelt werden — diese Mod muss der einzige
Nutzer dieses Query-Typs bleiben (die Occlusion-Queries der Engine sind `GL_SAMPLES_PASSED`,
kein Konflikt). Die Zeile erscheint auch in der Baseline-Mod: der CPU/GPU-Vergleich ist genau
die Sorte Zahl, für die die Messlatte existiert.

---

## „schlechtester … davon": wohin der Ausreißer-Frame ging

`schlechtester 72,47 ms` bei 10,6 ms Schnitt sagt: es gibt einen Ruckler — aber nicht,
woraus er besteht. Und in den geglätteten Mittelwerten ist die Ursache *prinzipiell*
unsichtbar: ein 30-ms-Schattenpass alle 180 Frames hebt den Schatten-Durchschnitt um
gerade 0,17 ms.

Deshalb schreibt `FrameStats` jedes Mal, wenn ein Frame neuer Fenster-Spitzenreiter wird,
dessen **komplette Buchführung** in einen Snapshot: alle Stage-Zeiten, Game-Tick, Upload,
„draußen" (Frame minus alles Zugeordnete — Swap, Frame-Limiter, Treiber-Rückstau) und das
GC-Pausen-Delta genau dieses Frames (`GC.GetTotalPauseDuration()`, einmal pro Frame gelesen,
kein GL-Sync). Die HUD-Zeile darunter zeigt die drei größten Posten:

```
schlechtester            72,47 ms
  davon                  opaque 41,2 + tick 18,3 + draussen 8,1 ms | gc 12,0
```

Der GC-Anteil steht bewusst hinter dem `|`: eine Pause friert die Stage ein, in der sie
landet — ein großer gc-Wert erklärt also den aufgeblähten Posten davor, statt zu ihm zu
addieren. Der Snapshot gehört immer zu exakt dem Frame, den `schlechtester` anzeigt
(veröffentlicht wird er zusammen mit dem Peak, beim Fensterwechsel wie beim Live-Anstieg).
Test füttert synthetische Frames über die `Advance(now, gcTotal)`-Naht und weist nach, dass
der Spike-Frame und nicht der Durchschnitt berichtet wird; Gegenprobe: Snapshot absichtlich
auf die Mittelwerte verbogen → Test fällt.

## Hitch-Log: jeder Ruckler einzeln, mit Kamerabewegung (1.31.0)

Der Worst-Frame-Snapshot hält genau *einen* Frame pro Fenster fest und ist weg, sobald das
Fenster rollt — für die Frage „warum ruckelt es **beim Umschauen**?" reicht das nicht.
Das Hitch-Log (`Measure/HitchLog.cs`) bucht deshalb **jeden** Frame, der die Schwelle reißt
(mindestens `HitchMinMs` = 15 ms **und** `HitchFrameFactor` = 2× des laufenden
Durchschnitts), einzeln: komplette Bucket-Aufteilung (before/schatten/opaque/oit/post/
ortho/done/tick/swap/draussen), GC-Pausen-Anteil, und — der eigentliche Punkt — die
**Dreh- und Bewegungsrate der Kamera in exakt diesem Frame**. Dazu, wenn der
Renderer-Profiler diesen Frame gesampelt hat (jeder vierte), der teuerste Renderer.

Mechanik: Die Erkennung läuft in `FrameStats.Advance`, solange die Frame-Buckets noch
intakt sind; der Eintrag wartet dann eine Frame-Grenze, weil das Kamera-Delta über den
Ruckler-Frame erst bekannt ist, wenn die nächste Grenze die Kamera sampelt. Die Raten
teilen durch die Frame-Dauer statt durch die Wanduhr — dadurch ist das Ganze mit
synthetischen Frames testbar. Yaw wird über die 0/2π-Naht gewickelt (sonst liest sich ein
5-Grad-Schwenk über die Naht als 8800 grad/s — Gegenprobe im Verify).

Ablesen:

- HUD-Zeile `ruckler 14 2,3/min` plus `zuletzt 31,2 ms, opaque 18,1, 215 grad/s`
- `.komet hitch` — Aggregat (wie viele beim Drehen / in Bewegung / im Stand / mit
  GC-Pause, dominante Buckets) plus die letzten acht Einträge; `.komet hitch reset` leert
- jede Buchung landet als eigene Zeile in `client-main.log` (max. 6 je 30 s, Rest gezählt)

Das Log liegt in `Measure/` und läuft in der Baseline identisch mit — „ruckler/min vanilla
gegen komet" ist damit per Konstruktion vergleichbar. Außerdem seit 1.31.0: HUD und
`.komet` nennen den **GC-Modus**.

Formatdetail (01.09. abends): die Klammer `(davon X warten auf threads)` steht jetzt
direkt hinter dem sweep-Wert, zu dem sie gehört. Vorher hing sie am Ende der
davon-Liste — ein Feldlog las dadurch `upload 0,2 (davon 2,6 warten auf threads)`, ein
Upload, der länger gewartet hätte als er lief. Die Position ist im Verify gepinnt.

Seit 01.09. (Nacht) trägt die Sweep-Angabe außerdem `(davon X rebuild, N pools)`, sobald der
Rebuild-Anteil mindestens 1 ms und ein Viertel des Sweeps ausmacht — dieselbe Regel wie
für die Thread-Wartezeit. Der Feldlog hatte Sweeps von 11-16 ms in Frames ohne GC-Pause
und ohne Warte-Vermerk; ob das Cache-Rebuilds nach Chunk-Entladungen waren (inzwischen
inkrementell, siehe „Entfernen ohne Rebuild") oder etwas anderes, sagt jetzt die Zeile.
`FastCuller` meldet nach jedem Cull-Aufruf die Rebuild-Ticks und -Zahl des Frames an
`FrameStats.AddCullRebuild`; die Baseline kennt den Aufruf nicht und meldet nichts.

### Korrektur: Server-GC war die falsche Empfehlung (1.46.0)

Bis 1.45.0 stand hier, Workstation-GC sei „der erste Ruckler-Verdächtige", und das HUD
mahnte ihn an. Grundlage war eine echte Messung — Server-GC senkte die Pausen von 131 auf
6 ms/s. Die Lesart war trotzdem zu eng: **ein Ruckler ist die längste einzelne Einfrierung,
nicht die Summe.** Server-GC erkauft seinen niedrigen Gesamtwert mit seltenen, dafür sehr
langen ephemeren Pausen — gemessen wurde eine von **65 ms in einer gen0-Sammlung**. Da hilft
keine Nebenläufigkeit: ephemere Sammlungen sind in *jedem* Modus stop-the-world, Hintergrund-
GC gilt nur für gen2. Dazu wollen die ~12 GC-Threads des Server-GC im selben Moment einen
Kern wie Render-, Tesselations- und Cull-Threads — auf 6 physischen Kernen der falsche Tausch.

Der zuvor getestete Verdächtige DATAS ist entlastet: `DOTNET_GCDynamicAdaptationMode=0` stand
zum Zeitpunkt dieser 65-ms-Pause bereits im Startskript.

Die HUD-Zeile mahnt seither nicht mehr, sondern nennt den Modus und die längste gemessene
gen0/gen1-Pause. Das Urteil fällt `HitchLog.GcModeVerdict` — ab 25 ms ephemerer Pause unter
Server-GC empfiehlt `.komet hitch` den Wechsel und nennt die Variable. `vs-launch.sh` hat
dafür `GC=server|workstation`; Default seit 30.08.2026 workstation.

**Nachtrag (1.51.0):** Die Reports 1.47/1.48 liefen trotzdem beide mit Server-GC
(`angefordert 1`, inklusive der 721-ms-gen1-Pause) — weil das **Desktop-Icon**
(`~/.local/share/applications/vintagestory.desktop`) noch `DOTNET_gcServer=1` aus der alten
Empfehlung setzte. Das Icon setzt jetzt `DOTNET_gcServer=0 DOTNET_gcConcurrent=1`, denselben
Stand wie der Launcher-Default. Lehre: eine revidierte Empfehlung ist erst revidiert, wenn
jeder Startpfad sie hat.

### Mesh-Puffer-Pool: die Allokationsquelle hinter den Lade-Rucklern (1.51.0)

Beim Chunk-Laden tragen die meisten Ruckler eine GC-Pause (1.47: 30 von 37), gespeist von
gemessenen **382 MB/s Allokation auf dem Tesselations-Thread** — ~1,3 MB je Chunk. Der
Tesselator schickt dabei jedes Chunk-Mesh durch `MeshData.CloneUsingRecycler`, die Engine
*hat* also ein Recycling-System — aber seine Ablage verliert Puffer auf genau zwei Arten,
und beide sind im Streaming-Muster am schlimmsten:

1. **Eine `SortedList` erlaubt einen Puffer je Größenschlüssel.** `TryAdd` probiert eine
   Handvoll Bruchteil-Schlüssel (+0,25er-Schritte) und **wirft den Puffer dann weg** — der
   fünfte gleich große Puffer eines Lade-Bursts wird entsorgt, auch wenn die Ablage fast
   leer ist.
2. **`TryGet` akzeptiert nur Treffer im Fenster [Größe, Größe×1,25+64].** Ein knapper
   Fehlgriff alloziert die vollen ~34 Bytes je Vertex neu, obwohl ein etwas größerer Puffer
   direkt daneben läge.

`FastMeshRecycler` (Default an) ersetzt die Ablage hinter derselben API durch
**Größenklassen** (×1,25 geometrisch, je Klasse eine LIFO-Liste, unbegrenzt viele gleiche
Größen): eine Anfrage rundet auf die Klasse auf und trifft nach dem Warmlaufen praktisch
immer. Verdrängt wird ältester-zuerst gegen ein Byte-Budget (`MeshRecyclerBudgetMb`,
Default 384 — Vanillas eigene Ablage hält laut eigenem Kommentar 300–400 MB, das ist also
kein neuer Speicher) plus dieselbe 15-s-TTL. Der Preis ist Schlupf: ein gelieferter Puffer
kann bis ~1,56× der Anfrage sein, wo Vanilla höchstens 1,25× übertrifft.

Am Bild ändert sich nichts — `CloneUsingRecycler` kopiert Inhalt und Zähler in den Puffer,
den es bekommt; der Patch entscheidet nur, *welcher* das ist. Einschalten übernimmt Vanillas
Bestand (auf dem Tesselations-Thread, dem einzigen, der die Listen anfassen darf);
Ausschalten gibt den eigenen Vorrat frei.

Erstmals ist die Trefferrate damit auch **messbar**: die Report-Zeile
`mesh-recycler: X% treffer, N MB vorgehalten, M MB frisch alloziert` zeigt live, wie viel
Allokation trotz Pool noch durchgeht. Bleibt `frisch alloziert` mit Pool hoch, sitzt die
Ladephasen-Allokation woanders — dann ist die Zeile die Widerlegung, nicht der Beleg.
`.komet toggle recycler` schaltet live um, die Stress-Phase `mesh-recycler aus
(vanilla-ablage)` misst den Unterschied (nur in Streaming-Szenen aussagekräftig — über
frisches Terrain fliegen, GC-Spalte mitlesen). Verify deckt Vertrag (Kapazität,
Index-Invariante, Recyclable), Wiederverwendung über Threads, TTL, Budget-Verdrängung
ältester-zuerst (klassenübergreifend), Vanilla-Übernahme und den Steady-State-Beweis
(100 Get/Dispose-Zyklen allozieren < 200 KB) ab; drei Mutations-Gegenproben (Verdrängung
invertiert, Treffer abgeschaltet, Übernahme entfernt) schlagen alle an.

### Klon-Kompakt: Kapazitätskopien der Custom-Parts (1.51.8)

Nach dem Mesh-Recycler (100 % Treffer) blieben auf dem Tesselations-Thread trotzdem
~255 MB/s Allokation. Zwei Runden Alloc-Attribution (1.51.6/1.51.7: erst nachbarn/licht,
dann klone/shapes) haben die Quelle auf `populateTesselatedChunkPart` eingegrenzt:
**217 von 255 MB/s im Part-Klonen** — und der Mechanismus steht im Dekompilat.
`CustomMeshDataPart.SetFrom` kloniert mit `Values.Clone()` das **komplette Array**, nicht
den Inhalt (`Count`). Zwei Multiplikatoren machen das teuer: die Akkumulationspuffer des
Tesselators wachsen auf das Hochwasser des größten je gebauten Chunks und schrumpfen nie —
und jeder Nicht-Liquid-Pass bekommt ein `CustomInts`, in das nie ein Wert geschrieben wird
(`Count` = 0). Jeder Chunk-Part kopierte also für immer hochwassergroße Null-Arrays.

`TightCustomClones` (Default an) ersetzt `MeshData.CloneExtraData` per Prefix durch eine
feldgetreue Kopie, deren Arrays nach `Count` bemessen sind — exakt das, was die
Pro-Face-Arrays im Original via `FastCopy` schon tun. Uploads hängen an
`Count`/`AllocationSize`, nicht an `Values.Length`; ein späteres `Add` wächst normal. Der
Patchpunkt ist bewusst die eine nicht-generische ~50-Zeilen-Methode (kein Inlining-Risiko,
keine Generics-Fallstricke). Report-Zeile `klon-kompakt` zählt Clones und gesparte
Kapazitätskopien; `.komet toggle tightclone` schaltet live auf vanilla zurück. Verify
prüft Inhalt, Layout-Felder, den Count-0-Fall und dass der Aus-Zustand exakt vanilla ist;
die Gegenprobe (Kapazität statt Count) schlägt an.

### Extras-Pool: was nach Klon-Kompakt noch frisch alloziert wurde (01.09.)

Nach Klon-Kompakt blieb `klone 18` von `tess 53 MB/s` (Report 01.09., 235 Chunks/s). Das
sind die Arrays, die inhaltsgroß, aber trotzdem frisch sind: die Pro-Face-Extras
(`XyzFaces`, `RenderPassesAndExtraBits`, die zwei Colormap-Id-Arrays) und die `Values` der
Custom-Parts — der Mesh-Recycler hält nur die Basis-Arrays (xyz, uv, rgba, flags, indices)
über Tesselationen hinweg, und der Engine-Kommentar über `CloneExtraData` sagt wörtlich,
diese „können nicht sinnvoll im Recycler bleiben". Also allozierte jeder Chunk-Part sie neu
und `Dispose` nullte sie direkt nach dem Upload. Für den Collector ist das die
unangenehmste Sorte Müll: auf dem Tess-Thread geboren, während der Wartezeit in der
Upload-Queue nach gen1 befördert, auf dem Render-Thread gestorben — genau die
Überlebenden, die eine gen1-Sammlung teuer machen.

Jetzt kreisen sie durch einen Größenklassen-Pool (Zweierpotenzen ab 16 Elementen, ein
Lock je Elementtyp, Byte-Budget `ExtrasPoolBudgetMb` = 64): gemietet in `CloneExtraData`,
wenn das Ziel ein Recycler-Mesh ist (`Recyclable` — d. h. ein Chunk-Part, der einzige
Aufrufer von `CloneUsingRecycler`), zurückgegeben von einem Postfix auf
`TesselatedChunkPart.AddToPools`, der einen Stelle, an der die Meshes eines Parts gerade
hochgeladen und disposed wurden. Der Prefix merkt sich die Array-Referenzen vor dem Upload,
der Postfix gibt genau die zurück — das Mesh hält sie da nicht mehr (`DisposeExtraData` hat
die Felder genullt), die Arrays sind also beweisbar unreferenziert. `MeshData.Dispose` selbst
ist absichtlich **nicht** gepatcht: klein genug, um bei Tier 1 in seine Aufrufer inliniert zu
werden — die Klasse still toter Patches, die dieses Projekt schon kennt.

Gemietete Arrays sind länger als der Count (Klassengröße). Nirgends in der Engine gilt die
Länge dieser Arrays als Zähler: die Uploads lesen `Count`/`BaseOffset`, die Wachstumspfade
vergleichen `Count` gegen `Length` und resizen bei voll, `AddMeshData` läuft nach Count.
Ein Array, das nach dem Klonen per `Array.Resize` gewachsen ist, hat keine Klassengröße mehr
und geht einfach an den Collector. Nicht-Recycler-Ziele (`Clone()`, Entity-/Item-Meshes)
bekommen weiterhin exakte, frische Arrays.

Report-Zeile `extras-pool: X% treffer (N anfragen), M MB vorgehalten, K verworfen` — bleibt
die Trefferquote bei 0 und die Anfragen steigen, feuert der Rückgabe-Postfix nicht (der
„idle sieht aus wie kaputt"-Fall, darum wird die Zeile gedruckt, solange der Pool scharf
ist). `.komet toggle extrapool` schaltet live um (aus = Vorrat frei), Stress-Phase
`extras-pool aus (frische arrays)`, Config `PoolMeshExtras`. Verify: Klassenrundung,
Wiederverwendung nach Referenz, Ablehnung von Nicht-Klassengrößen und Winzarrays, Budget,
Null-Count; dann der ganze Kreis über einen echten `TesselatedChunkPart` (Prefix →
`DisposeExtraData` → Postfix → zweiter Klon bekommt dasselbe Array), plus: ein
Nicht-Recycler-Mesh wird nicht erfasst, ein `Clone()` und der Aus-Zustand liefern exakte
Arrays.

### Zwei Feldfunde vom ersten fremden Tester (1.51.1 / 1.51.2)

Der erste Testbuild bei einem Dritten (Windows 10, i7-4770, RX 570) hat innerhalb eines
Abends zwei Fehler gefunden, die auf der Entwicklungsmaschine nie sichtbar waren:

**1. Crash beim Weltladen im ge-alt-tabbten Vollbild (1.51.1).** `SetupDefaultFrameBuffers`
gibt bei Fenstergröße 0 (Vollbild-Alt-Tab = minimiert) eine Liste **voller Null-Einträge**
zurück; `RebuildFrameBuffers` übernimmt sie und disposed die guten Buffer; der nächste Frame
stirbt in `ClearFrameBuffer(LiquidDepth)`. Die Engine ruft den Rebuild deshalb nie minimiert
auf (`Window_Resize` prüft den Zustand — im Tester-Log wörtlich: „Window was resized to 0 0?
… Will not rebuild frame buffers"). Unser erzwungener Rebuild für die größere Schattenkarte
(`ShadowResPatches.TryForceRebuild`, 500-ms-Tick nach Weltstart) hatte diesen Guard nicht.
Jetzt: `CanHostFramebuffers` (minimiert / `(int)(größe×ssaa) == 0` → warten), exakt die
Degenerat-Bedingung der Engine inklusive SSAA-Trunkierung; verify prüft das Prädikat,
Gegenprobe schlägt an. Der Retry wartet bis zu zwei Minuten und loggt, wenn er aufgibt.

**2. Das eigene HUD war das „ortho"-Stottern (1.51.2).** Ruckler-Signatur beim Tester:
~40 ms, fast alle im `ortho`-Bucket, ~3–4×/s, im Stand — exakt die feste 4-Hz-Kadenz des
Cairo-Textaufbaus, der auf dieser Maschine ~40 ms kostet (Entwicklungsrechner: ~1–2 ms,
darum nie aufgefallen; die Arithmetik schließt: 4×40 ms auf 96 fps = 1,7 ms Ortho-Schnitt,
gemessen 2,12). Fix in zwei Teilen: der Aufbau **misst sich selbst und drosselt sich**
(`NextIntervalSeconds` = 25× der eigenen Kosten, geklemmt auf 0,25–2 s — das Overlay darf
nie mehr als ~4 % der Wandzeit kosten), und jede Ruckler-Zeile trägt jetzt `hud X.X` wie
`sweep`/`upload` — ein HUD-verursachter Spike kann sich nie wieder als Engine-Problem
tarnen. HUD-Zeile `hud-aufbau` zeigt Kosten und Kadenz. Lehre, generalisiert: **jede
Diagnose-Anzeige braucht eine Selbstkosten-Attribution, sonst wird sie auf fremder
Hardware zum Befund.**

**Nachtrag (1.51.3): die 40 ms selbst beseitigt, nicht nur gedrosselt.** Der zweite
Tester-Log zeigte, dass die Drossel greift (Kadenz 1 s statt 4 Hz, `hud 35–44` sauber
attribuiert) — aber jeder einzelne Aufbau blieb ein 40-ms-Freeze und damit ~1 gebuchter
Ruckler pro Sekunde bei offenem HUD (52 von 100 Rucklern „ortho"). Die Kosten stecken im
Engine-Pfad `GenOrUpdateTextTexture`: dessen Autobreak-Layout misst jede Zeile **wortweise
gegen den wachsenden Zeilen-Präfix** (`ctx.TextExtents(prefix+wort)`, O(Wörter×Zeilenlänge)),
und jeder `CairoFont.GetTextExtents`-Aufruf zahlt obendrein einen kompletten Fontmap-Lookup
(`SelectFontFace` + `new FontOptions` pro Aufruf). Auf dem Win32-Cairo-Backend des Testers
summiert sich das auf ~40 ms; das FreeType-Backend hier kaschiert es mit ~1–2 ms. Dazu
löscht `LoadOrUpdateCairoTexture` die GL-Textur bei **jeder** Größenänderung und legt sie
neu an — und die breiteste HUD-Zeile enthält lebende Zahlen, die Breite wackelt also fast
jeden Aufbau um ein paar Pixel. Fix: das HUD rastert selbst (Monospace: Breite =
Zeichenanzahl × einmal vermessene Zellbreite, eine `MoveTo`+`ShowText` pro Zeile, kein
Autobreak, keine Vermessung pro Aufbau) in eine **wiederverwendete Surface mit
Hochwasser-Größe** in 64-px/4-Zeilen-Schritten (`NextSurfaceSize`, rechtsbündig verankert,
Überstand transparent) — der Upload nimmt damit praktisch immer den billigen
`glTexSubImage2D`-Pfad. `hud-aufbau` weist jetzt zusätzlich den Upload-Anteil aus, falls
doch einmal der Treiber der Teure ist. Verify prüft die Größenstabilität unter
Zahlen-Jitter; die Gegenprobe (Content-Größe direkt zurückgeben) schlägt an.

## Entity-Tesselations-Budget: der Umschau-Ruckler (1.32.0)

Der erste echte Hitch-Log-Lauf hat den Dreh-Ruckler benannt: `before 12–39 ms` bei
600–1000 grad/s **ohne** GC-Pause, Renderer `Before-ree` = `SystemRenderEntities`. Der
Mechanismus steht im Dekompilat: `EntityShapeRenderer.BeforeRender` beginnt mit
`if (!entity.ShapeFresh) TesselateShape()` — und `BeforeRender` läuft **nur für Entities im
Frustum**. Eine Entity, deren Shape offscreen stale wurde (oder nie gebaut war, frisch
gestreamt), tut also nichts, bis die Kamera sie erfasst — beim Schwenk über ein frisches
Gebiet läuft dann die Mainthread-Hälfte von `TesselateShape` für einen ganzen Schwung
Entities in einem Frame: Shape-Klon, `StepParentShape` je Kleidungs-/Rüstungsteil,
Textur-Baking mit Entity-Atlas-Insert (GL-Upload), Behavior-Handler. Das Voxel-Meshing
selbst geht danach an den ThreadPool — der Burst davor ist der Ruckler.

`EntityTesselationBudgetMs` (Default 2 ms, 0 = vanilla) deckelt diese Mainthread-Zeit pro
Frame; was drüber liegt, wird verschoben. Der Retry ist gratis und vanilla-eigen:
ein übersprungener Aufruf lässt `ShapeFresh` false, der nächste `BeforeRender` derselben
(sichtbaren) Entity ruft wieder — genau der Lazy-Pfad, den die Engine für Offscreen-Entities
sowieso fährt. Mindestens ein Aufruf pro Frame läuft immer (kein Verhungern), und der
Spieler ist strukturell außen vor: `EntityPlayerShapeRenderer` überschreibt
`TesselateShape()` ohne base-Aufruf, der Patch auf der Basismethode sieht ihn nie.
`.komet toggle enttess`, Safemode schaltet es ab, HUD-Zeile `entity-tess N verschoben`,
Stress-Phase `entity-tess-budget aus`.

**Das Budget muss nach vorn ausfallen (04.09.).** Sein Fenster wird ausschließlich von
`MeasurementPatches.FrameBoundary` zurückgesetzt. Bleibt dieses Event aus, während der Patch
angewandt ist, fällt `spentThisFrameMs` nie wieder unter das Budget — dann wird *jede* weitere
`TesselateShape` übersprungen, dauerhaft, für jede animierte Entity: „alle Tiere sind
unsichtbar", ohne Exception und ohne Logzeile. Zwei Riegel: `RequireFrameBoundary` lässt das
Feature erst gar nicht an, wenn die Messklammer nicht greifen konnte (Logzeile statt stiller
Fehlfunktion), und zur Laufzeit öffnet `StaleAfterMs` (250 ms ohne Boundary) das Fenster selbst
wieder — `StatStaleResets` zählt das. Dieselbe Regel im Entity-Load-Budget: ohne Drain hält
`Intake` nichts mehr zurück, sondern schließt sofort ab (`DrainStale`, `StatStaleFlushes`),
sonst erreichte keine einzige Entity je `LoadedEntities`. Ein Budget, das seinen Reset
verliert, darf auf Vanilla zurückfallen, nie auf „nie".

**Beifang desselben Logs, ein Messfehler seit 1.0:** `tick 26,6 ms` in einem 26,2-ms-Frame.
`CoreServerEventManager.TriggerGameTick` ruft `base.TriggerGameTick` — die gepatchte
Methode — auf dem **Server-Thread** (Singleplayer), und dessen Ticks landeten mit in der
Client-Frame-Buchhaltung. Seit 1.32.0 bucht der Postfix nur noch, wenn die übergebene Welt
`ClientMain` ist; die `game tick`-Zeile ist im Singleplayer jetzt ehrlicher (tendenziell
kleiner), besonders während Chunk-Fluten.

## „außerhalb … davon swap": wo die Treiberzeit wirklich sitzt

Mit mesa_glthread (radeonsi-Default) messen alle Stage-Zeiten nur das **Einreihen** der
GL-Befehle — der Treiber-Thread führt asynchron aus. Die echte Treiberarbeit wird dort
bezahlt, wo die Queue leerlaufen muss, und der eine garantierte Drain-Punkt jedes Frames ist
`SwapBuffers`. Ein `außerhalb` von 2,5 ms bei 32 % GPU-Auslastung ist also entweder
Treiber-Thread-CPU (Befehle ausführen), Compositor-Gegendruck — oder schlicht die
Event-Schleife. Seit 1.22.0 wird der Swap selbst gemessen: die HUD-Zeile zeigt
`= außerhalb … davon swap X`, und im Worst-Frame-Tail trennen sich `swap` und `draussen`
(= außerhalb minus swap: ProcessEvents, Maus, OpenTK-Dispatch).

Gemessen wird per **Transpiler auf `window_RenderFrame`**, nicht per Patch auf
`GameWindow.SwapBuffers`: das ist eine einzeilige nicht-virtuelle Methode, die der JIT in den
Aufrufer inlined — ein Prefix darauf würde sauber applizieren und nie laufen (die Lektion vom
toten Renderer-Profiler). Der Transpiler wirft, wenn er nicht genau einen SwapBuffers-Aufruf
findet; eine Messung, die stillschweigend nichts misst, ist schlimmer als keine.

**A/B-Hebel dazu: `.komet toggle glerror`.** ClientMain ruft zweimal pro Frame
`CheckGlErrorAlways` (nach Final-Compose und nach dem Blit) — jedes `glGetError` ist unter
glthread ein voller Queue-Drain mitten im Frame. Der Skip-Patch ist seit 1.22.0 immer
appliziert, aber laufzeit-gated (Default: aus = exakt Vanilla, inklusive der
Out-of-VRAM-Erkennung). Live umschalten, Stage-Zeiten und Swap-Anteil vergleichen — das
Bild bleibt identisch. Safemode schaltet den Skip mit ab.

Nebenbefund derselben Analyse: `vsyncMode 0` + `maxFps 241` heißt, weder VSync noch der
FPS-Limiter (`Thread.Sleep` in `window_RenderFrame`, nur aktiv bei `MaxFps < 241`) bremsen —
die Nähe von 12,41 ms zu 3×4,17 ms (240 Hz) war Zufall, keine Vblank-Quantisierung.

## Fenster-Pipelining: das eine echte Mehr-Thread-Stück der Pipeline

Die Render-Hälfte des Frames ist als Mod nicht parallelisierbar (ein GL-Kontext, ein
Thread, ~11.500 Renderer, die das annehmen). Die **Tesselation** hat dagegen eine seit
v1.9.2 vermessene, seit langem entworfene und wegen einer Klippe aufgeschobene
Pipelining-Stelle: `BuildExtendedChunkData` baut vor jedem Meshing das 34³-Fenster aus den
27 Nachbarchunks — **25–38 % der Chunk-Kosten**, strikt seriell vor dem Meshing. Seit
1.23.0 baut ein Worker das Fenster für Chunk n+1 (Vorhersage: vorderster Eintrag der
dirty-Queue, Priority zuerst), während der Tess-Thread Chunk n messht; bei Treffer werden
drei `Array.Copy` (~0,05 ms) statt des ~1,2-ms-Baus fällig.

**Die Klippe, jetzt gelöst:** `ClientChunkData.BuildFastBlockAccessArray` schreibt das
**statische** `BlockChunkDataLayer.blocksByPaletteIndex`, das `GetRange_Faster` über seine
Delegates liest — zwei gleichzeitige Fensterbauten korrumpieren sich stumm. Deshalb hält
jeder Fensterbau im Prozess (Worker UND Vanilla-Pfad, per Prefix/Finalizer) denselben
`BuildLock`. Deadlockfrei: unter dem Lock werden nur `chunksLock` (in
`GetNeighbouringChunks`, das selbst lockt) und die Chunk-eigenen Locks genommen, niemand
nimmt die Gegenrichtung.

**Staleness, in Vanillas eigenen Begriffen:** Vanilla liest Chunkdaten ohne Lock und heilt
Races per dirty-Retesselation — ein wenige ms früher gelesenes Fenster hat dieselbe
Semantik. Zwei Lücken, die NICHT heilen würden, sind abgedeckt: ersetzte `Data`-Objekte
(neu gesendeter Chunk → Referenzvergleich aller 27) und `SunRelightChunk` (läuft beim Pop
VOR dem Vanilla-Bau; ein Relight nach Baubeginn verwirft das Fenster).

**Korrektheit doppelt abgesichert:** (1) Die Füllreihenfolge ist als *Plan* transkribiert —
flache Int-Arrays der exakten GetRange/GetOne-Aufrufe; ein Verify-Test leitet die erwartete
Zelle→(Nachbar, Quellindex)-Abbildung unabhängig aus Weltkoordinaten her und weist nach:
jede der 39.304 Zellen exakt einmal, aus dem richtigen Chunk (Gegenproben: falscher
Quellindex und verschobene Center-Basis werden beide gefangen). (2) In-Game-Validierung:
die ersten 200 Treffer bauen das Fenster ZUSÄTZLICH auf dem Vanilla-Weg und vergleichen
elementweise — echte Weltdaten, echte Nebenläufigkeit; jede Abweichung deaktiviert das
Feature für die Sitzung und landet im Log. `TesselationPipelineValidateFirstN` steuert es,
`.komet toggle prebuild` schaltet live ab. HUD-Zeile `fenster-pipe` zeigt die Trefferquote;
der Gewinn steht direkt in der `nachbarn`-Spalte der tesselation-Zeile (1,1 ms → ~0 bei
Treffern).

---

## Zufluss und Verdauung ins Gleichgewicht bringen

Frische Welt, Sichtweite 1536, gemessen: **463 Chunks/s empfangen gegen 82/s tesseliert.**
Der Client ist nicht bloß im Rückstand — er ist um Faktor 5,6 im Rückstand, und alles, was der
Server über die Verdauungsrate hinaus produziert, ist aktiv schädlich:

* die Warteschlange wächst unbegrenzt (2.400 und steigend) und nichts davon ist dadurch früher
  auf dem Bildschirm,
* der Netz-Thread fügt ankommende Chunks unter genau dem `chunksLock` ein, das der Tesselator
  für jede Nachbarschaft braucht, die er liest,
* und auf sechs Kernen nehmen die Worldgen-Threads dem *einen* Tesselations-Thread die CPU weg.

Dieselbe Welt, einmal geladen und mit ruhender Worldgen: **234/s bei 3,9 ms pro Chunk** gegen
12 ms hier. Schneller zu produzieren als der Verbraucher schafft macht den Verbraucher langsamer.

`AdaptiveChunkInflow` (Default an, **nur Singleplayer**) beobachtet daher den Rückstand und
drosselt den Server auf einen Bruchteil seiner vollen Geschwindigkeit: unter
`InflowLowWaterChunks` (400) gar nicht, bei `InflowHighWaterChunks` (2000) auf 5 %, dazwischen
linear. Es gehen keine Chunks verloren und nichts wird in relevanter Weise verzögert — der
Client wäre ohnehin nicht dazu gekommen.

**Zwei Stellschrauben, nicht eine** — und das war der Fehler in 1.9.4: Die Spaltenzahl pro Tick
allein *kann* nicht hart genug bremsen. Sie endet bei 1, und eine Spalte pro 20-ms-Tick, mal
vier für eine lokale Verbindung, mal acht Chunks pro Spalte, erlaubt immer noch weit über
tausend Chunks pro Sekunde. Genau deshalb kamen bei einem angeblich „voll gebremsten" Client
gemessen immer noch **795 Chunks/s** an. Was die ganzzahlige Spaltenzahl nicht ausdrücken kann,
geht deshalb jetzt in das **Tick-Intervall** (`ChunkRequestTickTime`, ebenfalls bei jedem Tick
neu gelesen, gedeckelt bei 500 ms). Der `verify`-Test prüft, dass die *effektive* Rate
(Spalten pro Sekunde) monoton fällt und die Vollbremsung wirklich unter 20 % landet —
gegengeprüft, indem das Tick-Intervall wieder festgenagelt wurde: dann schlägt er fehl.

Das funktioniert nur, weil der integrierte Server denselben Prozess teilt: Rückstandszähler und
Server-Taktgeber sind dieselbe Speicherstelle. Im echten Multiplayer ist die Bremse aus, dort
kann ein Client nichts takten.

HUD-Zeile: `zufluss   35 %   2 spalten / 29 ms`.

---

## Sichtbare Löcher zuerst: Edge-Retess-Priorisierung

An der Ladefront wird ein Chunk gemesht, *bevor* sein Nachbar da ist — die gemeinsame Fläche
wird gegen das Unbekannte weggecullt und fehlt sichtbar im Bild, am auffälligsten auf der
Ozeanoberfläche. Die Engine repariert das mit einer **edge-only-Markierung** (Sign-Bit auf
dem Queue-Key), aber die landet am *Ende* von `dirtyChunks` — hinter tausenden
Voll-Tesselierungen von Chunks, die noch niemand sehen kann (~5 s bei gemessenen
371 Chunks/s). Das Koaleszieren dieser Marken (`EdgeRetessCoalesceMs`, seit 1.36 aus) hat
die Lücke noch verlängert und flog genau dafür raus; das hier ist die Gegenrichtung.

Ein Sweep auf dem Tesselation-Thread (Prefix auf `OnSeperateThreadGameTick`, höchstens alle
50 ms) rotiert `dirtyChunks` einmal und hebt bis zu 64 edge-only-Keys nach
`dirtyChunksPriority` — die der Konsument vollständig vor der normalen Queue abarbeitet und
deren Ergebnis am Vertex-Budget vorbei sofort hochgeladen wird. Eine Rotation statt
einzelner `UniqueQueue.Remove`-Aufrufe, weil Remove die Queue *pro Key* O(n) neu aufbaut.
Der Sweep ändert nur, *wann* eine Reparatur läuft, nie was sie produziert: derselbe negative
Key, derselbe Konsument, dasselbe `TesselateChunk(skipChunkCenter: true)` — Arbeit, die
vanilla ohnehin eingereiht hatte.

Zwei Schutzregeln, beide als Konstanten kodiert und im `verify`-Test festgeschrieben:
Spielerblock-Edits (volle Einträge in derselben Priority-Queue) dürfen nie begraben werden —
der Sweep steht still, sobald die Priority-Queue mehr als 192 Einträge hält, und schiebt pro
Durchgang höchstens 64 nach (schlimmstenfalls ~130 ms Randreparaturen vor einem Edit, nur
während Chunks fluten). Und die Beförderungskapazität (64 × 20/s = 1280/s) muss über dem
gemessenen Flut-Zufluss von ~1150 Randmarken/s liegen, sonst teilt sich der sichtbare
Rückstand nur in zwei Hälften — dieselbe Lektion wie die Catch-up-Regel des Koaleszierers.

Der Test fährt den reinen Kern gegen echte `UniqueQueue<long>`-Instanzen: Auswahl nur
negativer Keys unter Erhalt der Reihenfolge, Kappung mit Weiterbeförderung im nächsten Sweep,
Dedup gegen bereits dringende Einträge, und ein Erhaltungs-Fuzz (400 Runden Zufluss, Sweeps,
Konsum), der beweist, dass kein Key je verschwindet oder doppelt entsteht. Gegengeprüft mit
zwei realen Mutationen — Auswahl invertiert, Über-Cap-Keys verschluckt — beide schlagen an.

`.komet toggle edgeprio` schaltet live um; der A/B-Vergleich ist ein Blick auf die Ladefront,
keine Frame-Zeit. HUD-/Report-Zeile: `edge-prio N rand-reparaturen vorgezogen`.

---

## VRAM: leere Mesh-Pools zurückgeben

Ein `MeshDataPool` allokiert seine GPU-Puffer **im Voraus in voller Größe** — 500.000 Vertices
plus 750.000 Indizes, rund 10 MB — und die HUD-Zeile `terrain vram` ist schlicht Poolzahl mal
diese Größe. Pools entstehen nach Bedarf beim Streamen, aber **die Engine gibt nie einen
zurück**. Der Grund steht im Code: `AddModelDataPool` vergibt `poolId = modelPools.Count`, und
`RemoveLocationsNow` schlägt Pools als `modelPools[location.PoolId]` nach — der Listenindex
*ist* die Id. Einen Eintrag zu entfernen würde jede lebende Location auf den falschen Pool
zeigen lassen. Deshalb versucht es die Engine gar nicht erst.

Also bleibt der Listenplatz und nur der Speicher geht. Ein Pool, der lange genug leer ist,
bekommt seine Puffer gelöscht und die Kapazität auf 0 — ein Zustand, den die Engine ohnehin
sauber behandelt:

* `TryAppend` liefert `null`, wenn das Mesh nicht passt, und in 0 Kapazität passt nichts —
  `AddModel` geht dann zum nächsten Pool weiter, genau wie bei einem vollen.
* `TrySqueezeInbetween` läuft nur über 3 % Fragmentierung, und `CurrentFragmentation` ist bei
  `verticesPosition == 0` fest 0.
* `RenderMesh` wird nur bei `indicesGroupsCount != 0` aufgerufen, und ein Pool ohne Locations
  cullt immer auf 0 Gruppen — die gelöschte `MeshRef` wird also nie dereferenziert.

Die Wartezeit (`ReclaimEmptyPoolsAfterSeconds`, Default 20) ist wesentlich: beim Fliegen laufen
Pools ständig leer und füllen sich wieder; einen davon sofort einzuziehen tauschte VRAM gegen
eine Neuallokation Sekunden später. Nur Pools, die das ganze Fenster über leer bleiben — also
Gelände, das der Spieler hinter sich gelassen hat — sind es wert.

Läuft aus einem eigenen Renderer auf `EnumRenderStage.Done` (einmal pro Sekunde), weil das
Löschen eines Meshes einen GL-Kontext braucht; ein Game-Tick-Listener wäre zwar Main-Thread,
aber nicht vertraglich im GL-Kontext. Fehler schalten den Reclaimer ab statt den Frame zu
killen — Speicher zurückzugeben darf nie der Grund für einen Absturz sein.

HUD-Zeile: `vram frei   412 MB   17 pools zurueckgegeben`.

**Was das nicht ist:** eine Reduktion der eigentlichen Geometrie. Bei voll geladener Welt und
4 % Fragmentierung sind die 5,7 GB ehrliche Geometrie für Sichtweite 1536. Der Reclaimer holt
den *Verschnitt* zurück — die Pools, die eine frühere Ladephase brauchte und niemand wieder
hergab.

---

## Die kleineren Patches (GC-Druck, ehrlich gesagt Kleingeld)

| Patch | Was |
|---|---|
| `AnimationLookupWithoutAlloc` | `AnimatorBase.OnFrame` ruft `key.ToLowerInvariant()` pro aktiver Animation pro Entity pro Frame → je ein String. Das `animsByCode`-Dictionary bekommt einen `OrdinalIgnoreCase`-Comparer (genau das, was `ActiveAnimationsByAnimCode` in der Engine schon benutzt), ein Transpiler löscht die Aufrufe. |
| `AnimationCollisionBoxWithoutAlloc` | `AnimationManager.OnClientFrame` macht `ActiveAnimationsByAnimCode.Any(…)`. `Enumerable.Any` geht über `IEnumerable<T>` und **boxt** dabei den Struct-Enumerator des Dictionary — eine Allokation pro animierter Entity pro Frame. Wird zur normalen Schleife. |
| `LowLatencyGC` | Fordert `GCSettings.LatencyMode = SustainedLowLatency` an: der Collector macht dann keine blockierenden, kompaktierenden Gen2-Sammlungen mehr, während gespielt wird — genau die, die lang genug für einen ausgefallenen Frame sind. Der Preis ist ein etwas größerer Heap, der Collector schiebt die Arbeit ja nur auf. Ausschalten, wenn der Client bei deiner Sichtweite ohnehin am Speicherlimit kratzt. Wird die Anfrage abgelehnt, steht das im Log und sonst passiert nichts. |
| `SkipPerFrameGlErrorCheck` | **Default aus.** `ClientMain` ruft 2× pro Frame `glGetError()`. Mit `mesa_glthread` (dein `vs-launch.sh` setzt das) ist das ein harter Sync-Punkt mit dem GL-Worker-Thread. Aus lassen heißt aber auch: die Engine erkennt GL-OOM nicht mehr und du bekommst statt „reduce your view distance" einen Treiber-Crash. Nur einschalten, wenn du misst, dass es bei dir was bringt. |

Die Animations-Patches sind Millisekunden-Bruchteile, keine FPS-Wunder. Sie sind drin, weil sie
sicher und kostenlos sind — nicht weil sie den Unterschied machen. `LowLatencyGC` zielt nicht
auf den Mittelwert, sondern auf die Ausreißer: es macht die Frame-Zeit nicht kleiner, sondern
gleichmäßiger.

---

## Entity-Laden: Budget und Nächste-zuerst (02.09.)

Wie Entities beim Client ankommen, aus dem Dekompilat: **nicht** mit dem Chunk-Paket (das
`Entities`-Feld von `Packet_ServerChunk` bleibt in 1.22 leer) und **nicht** über die
`EntityLoadQueue`, die `ClientSystemEntities.OnGameTick` jeden Tick leert — die hat keinen
Produzenten mehr, beides sind tote Pfade. Tatsächlich beginnt der Server ein Entity zu
*tracken*, sobald dessen Chunk gesendet ist (`PhysicsManager`, alle 200 ms), und schickt dafür
ein volles Entity-Paket (Id 33) je Entity; Spawns kommen als Paket 34. Jedes davon wird auf dem
Client eine Main-Thread-Task, und die macht alles auf einmal: Entity erzeugen, deserialisieren,
`Initialize` (Behaviors, Animator, Attribute), im Chunk registrieren, in `LoadedEntities`
eintragen, `OnEntityLoaded` feuern — was den Renderer anlegt. Beim Weltbeitritt und bei jedem
Schub frisch gestreamter Chunks landen so Hunderte davon in **einem** Task-Drain, in der
Reihenfolge, in der der Server sein Dictionary gerade durchlief.

`EntityLoadPatches` trennt an der Stelle, an der Vanilla selbst trennt: Erzeugen und
`FromBytes` (billig, und es liefert die Position) laufen sofort beim Paket; der teure Rest —
`Initialize`, Chunk-Registrierung, Renderer — wartet in Distanz-Bins (32 Blöcke je Bin) und wird
an der Frame-Grenze unter `EntityLoadBudgetMs` (1,5 ms/Frame) abgearbeitet, nächstes Entity
zuerst. Liveness wie bei allen Budgets der Mod: mindestens zwei je Frame, ein Rückstau kann nie
verhungern; `.komet toggle entload` aus = alles Gehaltene wird sofort fertiggestellt.

Kohärenz mit jedem anderen Paket, das ein Entity nennt, ist exakt: Attribute (37/38/60),
Custom-Pakete (67), die Spawn-Position (80), das Bulk-Paket (40) und ein wiederholtes 33/34
stellen ein gehaltenes Entity **vor** dem Vanilla-Handler fertig, so dass dessen
`LoadedEntities`-Lookup genau so trifft wie ohne Budget; ein Despawn (36) für ein gehaltenes
Entity verwirft es (es war nirgends registriert). Beobachtbar ändert sich nur, *wann* ein
entferntes Entity in einem Schub erscheint — dieselbe Klasse von Änderung wie das
Prio-Upload-Budget für Chunk-Meshes. Ruckler-Zeilen tragen `entload X`, Report-Zeile
`entity-laden`, HUD-Zeile `entity-laden`.

Erster Feldreport (02.09., Join-Flut, 3 min): 1.608 geladen, davon **1.188 vorgezogen** — das
Budget griff nur bei jedem vierten Entity. Ursache: fast jedes frisch getrackte Entity bekommt
binnen 200 ms ein Attribut-Update (Paket 38/60), und das stellte das gehaltene Entity sofort
fertig. Seitdem werden Attribut-Pakete (37/38/60) direkt in den Baum des gehaltenen Entities
gefaltet (`FromBytes` bzw. `PartialUpdate`, dieselben Aufrufe wie Vanilla auf einem geladenen
Entity); der Vanilla-Handler findet die Id danach nicht in `LoadedEntities` und tut nichts —
äquivalent zu einem etwas später gesendeten vollen Paket. Nur 40, 67 und 80 brauchen ein
initialisiertes Entity und stellen weiterhin sofort fertig.

## Minimap: Kachel-Uploads unter Budget (02.09.)

Der Tick-Profiler nannte im ersten Report den dominanten Bucket der Join-Flut beim Namen:
127 von 339 Rucklern waren `tick`, jeder mit `tick-listener WorldMapManager.OnClientTick` bei
2,5 bis 8,5 ms. Mechanismus aus `ChunkMapLayer` (VSEssentials): Kacheln (ein 32×32-Bild je
Chunk-Spalte) entstehen auf einem Worker und landen in einer Queue; `OnTick` holt bis zu **200**
davon je 20-ms-Tick und macht je betroffener 3×3-Komponente einen Framebuffer auf, lädt jede
Kachel als Textur hoch und zeichnet sie in die 96×96-Textur. Beim Streamen (jede geladene
Spalte reiht sich selbst und vier Nachbarn ein) sind das Hunderte Uploads und Draws in einem
Tick, auf dem Main-Thread — für ein HUD-Element, das niemand so schnell braucht.

`MinimapPatches`: ein Transpiler ersetzt die Konstante 200 durch `PiecesPerTick()`, und ein
Prefix/Postfix-Paar misst jeden Tick, der Kacheln entnommen hat. Die Kappe halbiert sich, wenn
der Tick über 1,5× `MinimapPieceBudgetMs` (1 ms) lag, und verdoppelt sich bis auf Vanillas 200,
wenn er unter der Hälfte blieb — die Flut läuft mit ~1 ms je Tick, eine geöffnete Weltkarte auf
leerem Frame füllt sich weiter mit Vanilla-Tempo. Nichts geht verloren, die Kacheln bleiben in
der Engine-Queue. `.komet toggle minimap`, HUD/Report-Zeile `minimap`, Stress-Phase
`minimap-budget aus (200/tick)`. Aus = exakt Vanilla (die Funktion liefert dann 200).

## Hauptthread-Tasks und Tick-Listener: die zwei namenlosen Buckets (02.09.)

Zwei Buckets des Hitch-Logs hatten bis jetzt keinen Besitzer:

**`draussen`** ist alles zwischen den Render-Stages und der nächsten Frame-Grenze — und dort
läuft `ClientMain.ExecuteMainThreadTasks`, das jedes Server-Paket außer Chunk-Daten als
`ClientTask` (`readpacket33`, `readpacket38`, …) ausführt: Entity-Loads, Block-Updates,
Attribut-Syncs, Inventar. Ein Schub davon las sich bisher als Treiber-Back-Pressure, weil er
nicht vom Swap zu unterscheiden war. `MainThreadTaskPatches` ersetzt den Drain durch eine
1:1-Transkription mit einer Uhr um jede Task (gleicher Lock, gleiche Suspend/Requeue-Regel,
gleiche Profiler-Marks, Exceptions propagieren wie vorher). Der Frame behält Summe und
schwerste Task (`tasks 9,1 (readpacket33 8,2)` in der Ruckler-Zeile), eine geglättete Tabelle
je Code steht im Report (`hauptthread-tasks: …`). `.komet toggle mtt` gibt den Drain an Vanilla.

**Der Lock ist nicht auf jedem Client vom selben Typ (05.09.).** Vanilla deklariert
`ClientMain.MainThreadTasksLock` als `object` und nimmt ihn mit `Monitor`; der Optimum-Build
(v0.3.14) als `System.Threading.Lock` und betritt ihn mit `EnterScope`. Eine kompilierte
Feldreferenz trägt den Feldtyp in ihrer Signatur — die Vanilla-Bindung fand das Feld auf dem
Fork nicht mehr (`MissingFieldException` im ersten Frame des Verbindungsbildschirms,
1.2.0-pre.3). Die Transkription liest das Feld jetzt per Namen (einmal je Session, nicht je
Frame) und nimmt es so, wie sein Typ es verlangt (`QueueLock`): `Monitor` auf dem Objekt,
`Enter`/`Exit` auf dem `Lock`. Das ist keine Bequemlichkeit: `Monitor.Enter` auf einer
`Lock`-Instanz ist ein *anderes* Lock als das, das `EnqueueMainThreadTask` auf dem
Netzwerk-Thread hält — die Übergabe liefe ohne jede Exception gegen den Enqueue. Ein dritter
Typ lässt den Drain bei Vanilla („could not enable"). Verify prüft beide Formen
(`Monitor.IsEntered` bzw. `Lock.IsHeldByCurrentThread`, und dass der `Lock` *nicht* über
Monitor genommen wurde) und lässt den ganzen Drain samt Suspend/Requeue gegen ein echtes
`ClientMain` mit dem Feld der Engine laufen. Gefunden mit einem Bindungs-Check: jede Methode
der gebauten Komet.dll einmal gegen Optimums Assemblies JIT-kompiliert
(`RuntimeHelpers.PrepareMethod`) — genau zwei scheiterten, `Execute` und `Requeue`, beide an
diesem Feld; sonst weicht die Bindung nicht ab.

**`tick`** gehört den Tick-Listenern (`EventManager.GameTickListenersEntity`, ein paar Dutzend
System- und Mod-Listener) plus allen Block-Entity-Ticks. `TickProfiler` wickelt jeden dieser
Listener-Delegates in einen Timer — die Liste wird per Id verwaltet, Identität ist hier kein
Problem —, die Ruckler-Zeile nennt den teuersten (`tick-listener ClientSystemEntities.OnGameTick
7,5 ms`), der Report rangiert sie (`tick-listener: …`). Block-Listener (Tausende bei 1536)
werden bewusst nicht gewickelt; ihr Anteil ist, was die Summe nicht erklärt. Solange der
Engine-eigene Tick-Profiler (`extendedDebugInfo`) läuft, wird entwickelt, weil der die
Listener über den Target-Typ des Handlers benennt. `.komet toggle tickprofiler`.

Beides sind Diagnosen, nicht Optimierungen, und beide stehen als Stress-Phasen im Plan
(`task-attribution aus`, `tick-profiler aus`), damit ihr Preis gemessen ist statt angenommen.

## Server-Seite: Entity-Sync (02.09.)

Im Singleplayer teilt sich der integrierte Server Prozess und GC mit dem Client — jedes Paket,
das er nicht baut, ist Müll, den der Client-Frame nicht einsammelt. `EntitySyncPatches` (Harmony-
Id `komet.server`, läuft auch auf einem dedizierten Server mit Komet) transkribiert vier
Methoden aus `PhysicsManager` mit je einer zusätzlichen Bedingung:

1. **Distanzabhängige Senderate.** Vanilla schickt jedem Client für jedes getrackte Entity, das
   sich bewegt hat, in jedem Physik-Tick (30 Hz) ein Positionspaket. Bis 40 Blöcke bleibt das so,
   bis 80 Blöcke gehen die Pakete mit 15 Hz raus, darüber mit 10 Hz. Der Client interpoliert
   ohnehin zwischen Snapshots (`EntityBehaviorInterpolatePosition`), der Tick-Zähler im Paket
   verträgt Lücken (UDP), Teleports werden nie ausgedünnt.
2. **Tracking-Hysterese.** Ein Entity ist getrackt, solange es im Radius liegt, und untracked
   im Moment, in dem es draußen ist — eines, das um die Grenze wandert (oder ein Spieler, der
   hin- und hergeht), bekommt so immer wieder volles Paket, Client-Erzeugung und Despawn. Jetzt
   bleibt ein bereits getracktes Entity getrackt, bis es 15 % jenseits des Radius steht.
   Simulationszustand und `IsTracked` bleiben exakt Vanilla; nur die Client-Liste kennt das Band.
3. **Nächste zuerst am Cap.** Greift `TrackedEntitiesPerClient`, lässt Vanilla neue Entities in
   Dictionary-Reihenfolge zu; jetzt gewinnen die nächsten.
4. **Attribut-Sync ohne No-Ops.** Alle 200 ms werden je Entity die dirty Attribut-Pfade
   serialisiert und gesendet; Server-Code markiert bei jedem `Set` dirty, ob der Wert sich
   änderte oder nicht. Pfade, deren Bytes dem zuletzt gesendeten Stand gleichen, fallen weg, ein
   leer gewordenes Paket wird nicht gebaut. Der Cache wird bei jedem vollen Entity-Paket
   invalidiert (ein neu trackender Client bekommt den kompletten Baum), also kann kein Client auf
   einem A-B-A-Wert hängen bleiben. Client-seitige Listener feuern für unveränderte Werte nicht
   mehr — die Engine-Konvention für ereignisartige Attribute ist aus genau dem Grund ein Zähler
   (`onHurtCounter`).

Delta-Kodierung der Positionen ist bewusst **nicht** gebaut: das Wire-Format hängt an beiden
Seiten, die Ersparnis wäre Bandbreite, und die ist über Loopback keine Größe — die Allokation
je Paket bliebe.

`.komet toggle entsync|attrskip`, Report-Zeile `entity-sync (server): positionen … gespart,
hysterese-halte, attribute … gespart, pakete unterdrueckt`, Stress-Phasen `entity-sync-tuning
aus (server)` und `attribut-noop-skip aus (server)`. Auf einem fremden Server ohne Komet stehen
die Zähler auf Null und die Zeile sagt es.

## Schattenkarten-Rebuild: nie mehr aufgeben (02.09.)

Der erzwungene Framebuffer-Rebuild für `ShadowMapExtraQuality` gab nach 240 Versuchen (zwei
Minuten) auf. Am 01.09. lief eine ganze Session mit vanilla-großer Karte, das Log sagte
„window never ready" und nichts darüber, *warum*. Jetzt sinkt die Kadenz nach zwei Minuten auf
fünf Sekunden und der blockierende Grund wird einmal geloggt (Engine unterdrückt Reloads, Fenster
minimiert, Fenstergröße/SSAA kann keine Framebuffer, Größenausdruck nie erreicht) — ein
minimiertes Fenster durch die ganze Ladephase ist legitim, eine still klein gebliebene
Schattenkarte nicht.

## Minimap: direkter Kachel-Upload statt Framebuffer (02.09., Runde 3)

Der zweite Report zeigte die Kappe am Boden (8 Kacheln je Tick) und trotzdem 1,14 ms je
Upload-Tick — 0,14 ms für ein 4-KB-Bild. Das ist, was Vanillas `FinishSetChunks` je Kachel
kostet: ein Framebuffer-Objekt je Komponente je Tick angelegt und wieder zerstört, ein
Shader-Wechsel, die 32×32-Kachel als Staging-Textur hochgeladen **mit Mipmaps**, ein Quad
durch das `texture2texture`-Programm gezeichnet, FBO abgebaut. Alles, um 32×32 Pixel in ein
Rechteck einer 96×96-Textur zu kopieren — also genau ein `glTexSubImage2D`.

Die Orientierung ist aus dem Shader belegt: `texture2texture.vsh` spiegelt nicht
(`posTL = (pos+1)/2; posTL.y = ys + posTL.y*height`), das Quad trägt an Vertex (-1,-1) die
UV (0,0), `ys` liegt an der unteren Kante des Zielrechtecks in Framebuffer-Zeilen. Kachel-Zeile
r landet also auf Textur-Zeile 32·(i/3)+r, Spalte 32·(i%3)+c — dieselben Zeilen, die der
Sub-Image-Upload schreibt. `MinimapPatches.FinishSetChunksPrefix` ersetzt den Draw dadurch,
Textur-Anlage (96×96 aus dem geteilten Leer-Puffer) und Mipmap-Regeneration danach bleiben
Vanilla. Einziger semantischer Unterschied: der Alpha-Test des Shaders (Quellpixel unter 0,005
Alpha wurden übersprungen, der alte Pixel blieb) — eine Kachel hat solche Pixel nur, wo die
Regen-Höhenkarte außerhalb des Bereichs liegt, und das sind in jeder Generation derselben
Kachel dieselben Pixel. Die Kappe klettert damit von selbst wieder auf 200.
`.komet toggle minimapdirect`, Stress-Phase `minimap-direktupload aus (FBO)`, Report-Zeile
`minimap: … direkt-upload (N kacheln in M komponenten)`.

## Task-Drain: Zeitbudget mit Vanillas Requeue (02.09.)

Mit der Task-Attribution hatte der `draussen`-Bucket im zweiten Report Namen:
`tasks 16,9 (loadchunk 16,8)` und `tasks 13,8 (loadchunk 12,4)`. Der Netzwerk-Thread reicht dem
Drain einen ganzen Chunk-Schub auf einmal (Singleplayer: Paket 10 läuft als Objekt über
`DummyNetConnection`, der Client-Netz-Thread packt aus und reiht je Chunk eine `loadchunk`-Task
ein), und `ExecuteMainThreadTasks` lief sie alle in einem Frame ab.

`MainThreadTaskPatches.RunTasks` hat jetzt ein Budget (`MainThreadTaskBudgetMs`, 3 ms): ist es
überschritten, geht der Rest mit **Vanillas eigenem Requeue** zurück — der Suspend-Pfad, den
`SuspendMainThreadTasks` auch nimmt: der Rest kommt an den **Anfang** der geteilten Queue, vor
alles, was inzwischen ankam. Reihenfolge bleibt, nichts geht verloren, ein Paket, das einen
Chunk referenziert, läuft weiterhin nach dessen `loadchunk`. Liveness: mindestens 8 Tasks laufen
immer; das Budget dehnt sich mit dem Rückstau (×2 bei 256 wartenden Tasks) und wird über 4096
ignoriert — eine Queue, die schneller wächst, als das Budget abträgt, darf nicht unbegrenzt
wachsen. Reine Regel `OverBudget(budget, spent, ran, remaining)` im Verify.
`.komet toggle taskbudget`, Stress-Phase `task-budget aus`, Report: `hauptthread-tasks: …
budget 3 ms: N frames gekappt, M tasks verschoben`, HUD `mt-tasks · N frames gekappt`.

**Das Budget gilt erst ab `LevelFinalize` (04.09.).** Ein Bericht zu 1.2.0-pre.2 zeigte beim
Weltbeitritt eine `NullReferenceException` in `WeatherSystemClient.OnRenderFrame` — Vanilla
registriert den Renderer früh, baut sein `WeatherDataAtPlayer` aber erst in
`LevelFinalizeInit`, und der Level-Finalize-Handler ist selbst eine Hauptthread-Task
(`readpacket6=LevelFinalize`). Über Frames verteilt heißt das: der Before-Stage-Renderer läuft
ein Frame, bevor das Paket, das ihn initialisiert, an der Reihe war. Das Beitritts-Gedränge ist
Lebenszyklus, keine Last — `MainThreadTaskPatches.WorldReady` (gesetzt im `LevelFinalize`-Hook
des Mod-Systems, gelöscht beim Verlassen in `Detach`) hält den Drain bis dahin auf Vanilla:
alles, was ankommt, läuft im selben Frame. Verify prüft beide Seiten (ungebudgetiert vor
`LevelFinalize`, gekappt danach, zurück auf Vanilla nach `Detach`).

## Server-Allokation: die 193 MB/s bekommen Namen (02.09.)

Jede gen0-Sammlung pausiert den Render-Thread, egal wer alloziert hat. Die Join-Flut-Reports
vom 02.09. maßen 279 MB/s, davon 193 unattribuiert (`rest = ungemessen, v.a. integrierter
server`), 35 gen0/s, und 52 von 54 Rucklern saßen auf einer GC-Pause. Der größte verbliebene
Hebel liegt auf der Server-Seite des Prozesses — und hatte keinen Namen. Die GC-Konfiguration ist
als Hebel ausgemessen und tot (gen0size und Server-GC: seltener, aber längere Pausen; siehe
Ruckler-Kapitel), also bleibt nur: weniger allozieren, und dafür erst messen, wer.

`ServerAllocPatches` (Server-Harmony-Id, in Singleplayer im selben Prozess) legt
`GC.GetAllocatedBytesForCurrentThread` um die Arbeit — dasselbe Werkzeug wie `netz`/`prefetch`
auf der Client-Seite. Thread-Ebene (disjunkt, je ein Thread): `tick` (`ServerMain.Process`),
jede `ServerThread`-Schleife über ihren Namen (`chunkdb`, `compress`, `relight`, `blockticks`),
`worldgen` (die zusätzlichen Worldgen-Threads, `GenerateChunkColumns_OnSeparateThread`),
`physik-worker` (`PhysicsManager.DoWork`) und `physik-helper` (seine Queue-Aktionen, gewickelt).
Darunter Verdächtige **innerhalb** dieser Threads: `send-chunks` (Chunk-Pakete bauen),
`entities`, `physik-tick`, `mt-tasks`, `worldgen-passes` (`runGenerators`), `db-laden`
(`TryLoadChunkColumn`), `chunk-einbau` (`mainThreadLoadChunkColumn` — trotz Namens auf dem
Chunk-Thread). Bytes gehören dem Thread, der den Code ausführte, also steckt jeder Verdächtige
in genau einer Thread-Summe. Raten werden einmal je Sekunde aus dem Server-Tick gefaltet
(Interlocked-Zähler, jeder Thread bucht selbst).

Im Report: `alloc-quellen: netz …, server N, rest M` — die Thread-Summe verlässt die
Rest-Spalte —, darunter `alloc server: chunkdb 120, tick 40, … MB/s | davon worldgen-passes 90,
send-chunks 30, …`. Messung, kein Eingriff; `.komet toggle serveralloc`. Der nächste Report
entscheidet, welcher Allokator als Erster dran ist.

## Entity-Before-Stage: Attribution und Animations-LOD (02.09.)

Drei Ruckler des zweiten Reports hießen `before 17-20 ms | renderer Before-ree 17-19 ms`, davon
nur 1,2-1,5 ms `enttess`. `SystemRenderEntities.OnBeforeRender` läuft je Frame über **alle**
geladenen Entities: Frustum-Test, `EntityRenderer.BeforeRender` für die sichtbaren
(Shape-Tesselation, zwei Licht-Lookups), dann `AnimManager.OnClientFrame` für alle — und das
treibt den Animator (Gelenk-Matrizen, die teuerste CPU-Arbeit je Entity) für jede Entity, die
gerendert, schatten-gerendert oder tot ist. Bei 255 Blöcken Schattenreichweite ist fast jede
geladene Entity schatten-gerendert.

`EntityAnimPatches` ersetzt die Schleife durch eine 1:1-Transkription mit zwei Uhren
(`vor-render` / `anim`), zählt die Entities je Hälfte und behält die teuerste einzelne Entity
des Frames. Ruckler-Zeile: `entities vor-render 2,1 ms, anim 9,3 ms/188 (top
wolf-eurasian-adult-male 1,2)`; Report `entity-before: …`, HUD `entity-anim`. Provider-Muster
wie der Tick-Profiler (`HitchLog.EntityFrameProvider`, gelesen bei der Ruckler-Erkennung, vor
`EndFrame`).

Die Optimierung darauf: eine Entity, von der nur der **Schatten** gerendert wird (außerhalb des
Sicht-Frustums, innerhalb des Schatten-Frustums), bekommt ihre Animation jeden dritten Frame,
eine gerenderte Entity jenseits `EntityAnimationFarBlocks` (48) jeden zweiten — jeweils mit dem
dt der übersprungenen Frames aufaddiert, die Animations-**Zeit** läuft also exakt wie vorher,
nur seltener abgetastet. Eigener Spieler, nahe Entities und tote (ihre Todesanimation muss zu
Ende laufen; Vanillas eigenes Gate `!Alive` bleibt) immer in voller Rate. Die Reihen werden über
die Entity-Id verteilt (`IsTurn(frame, id, divisor)`), damit die gesparte Arbeit gleichmäßig
über die Frames liegt statt jeder dritte Frame billig zu sein. Reine Regeln `Divisor` und
`IsTurn` im Verify. `.komet toggle animlod` (Rate), `.komet toggle entbefore` (die ganze
Transkription, ohne sie auch kein LOD), Stress-Phase `anim-lod aus`.

## Benutzung

```bash
cd komet
./build.sh          # bauen + alle Checks (Patch-Anwendung, Verhalten, Äquivalenz, Benchmark)
./build.sh deploy   # dll nach ~/.config/VintagestoryData/Mods/
./build.sh bench    # nur der Benchmark
./build.sh config   # dist/komet.json aus der echten Config-Klasse neu schreiben
```

Deinstallieren: `rm ~/.config/VintagestoryData/Mods/Komet.dll`

### Das F7-HUD (Layout seit 01.09.)

**F7 schaltet in drei Stufen: aus → kompakt → voll.** Die Kompaktansicht ist die
Spieler-Sicht — fps, gpu-frame, Ruckler mit „zuletzt", GC-Pausen, eine Lade-Zeile nur
solange die Welt streamt, dazu immer die !!-Warnungen (Safemode/Stresstest/Diagnose,
damit keine Ansicht eine Safemode-Sitzung wie eine normale aussehen lässt). Alles darin
ist eine **Auswahl** der Vollansicht, nie eine andere Messung. Die Vollansicht bleibt das
Diagnose-Instrument; Screenshots bleiben vergleichbar. Die Baseline-Mod zeigt immer die
Vollansicht — sie existiert für Vergleichsbilder. Der Commit-Teil des Buildstempels wird
auf die üblichen sieben Zeichen gekürzt (`b260901.1928.577893d` statt 40 Hex-Zeichen —
das SDK hängt den vollen Hash an die InformationalVersion).

Vollansicht: vier Blöcke statt einer flachen Liste: **Kopf** (fps, gpu-frame,
schlechtester Frame mit Aufteilung, Ruckler), **frame-aufteilung** (jeder Bucket des
Frames in der Reihenfolge und mit dem Vokabular des Hitch-Logs — inklusive game tick und
`außerhalb` summiert der Block auf 100 %), **gc** (Pausen, Alloc-Quellen, Modus) und
**welt & laden** (Draw Calls, Chunks, Tesselation, VRAM, Upload). Neben jedem
Frame-Bucket steht ein Balken: zehn Zellen sind der ganze Frame, Achtel-Zellen über die
Unicode-Blockelemente. Ob die Blockglyphen in der Monospace-Zelle des Systems wirklich
eine Zelle breit sind, wird einmal beim Metrik-Probing gemessen; weicht der Font ab,
degradieren die Balken zu `#`, statt aus der Box zu laufen (die Rasterbreite rechnet in
Zeichenzellen). Die Schatten-Zeilen der Aufteilung enthalten jetzt auch die Done-Hälften
der Kaskaden, damit zwischen den Zeilen nichts mehr versteckt ist; die Drossel-Zeile der
Komet-Sektion heißt `schatten-takt`, damit kein Name zwei Bedeutungen hat.

**F7 ohne Flackern, HUD-Aufbau ohne Frame-Kosten (01.09. abends).** Zwei Funde aus
demselben Feldlog. Erstens flackerte beim Zyklus voll → aus → kompakt kurz die volle
Ansicht auf: der Text-Rebuild lief nur auf Timer (0,25–2 s), F7 änderte den Zustand
sofort, aber die **alte Textur** wurde bis zum nächsten Timer-Rebuild weitergezeichnet.
Jetzt invalidieren die View-Properties selbst (`dirty`), und eine kleine Zustandsmaschine
(`NextStep`, pur, von verify gepinnt) erzwingt die Invariante: **ein dirty-Frame zeichnet
nie die alte Textur** — er baut sofort synchron neu (der F7-Pfad, einmalig ~2 ms beim
Tastendruck) oder zeichnet, falls gerade ein Raster in Flug ist, für ein bis zwei Frames
gar nichts. Unsichtbar schlägt falsch.

Zweitens stand der HUD-Aufbau selbst im Ruckler-Log (`hud 3,0 / 3,1 / 7,7`): Sampling +
Cairo-Raster + GL-Upload landeten komplett in einem einzigen Ortho-Frame. Der wiederkehrende
4-Hz-Refresh rastert jetzt **im Worker** (`Task.Run`): der Frame zahlt nur noch Sampling +
Compose beim Start und den Upload ein paar Frames später, wenn der Worker fertig ist —
die Textur zeigt solange die 250 ms alten Zahlen, was bei einer 4-Hz-Anzeige niemand sieht.
Wirft Cairo auf dem Worker (Plattform ohne Thread-Support), fällt das HUD für die Sitzung
auf den synchronen Pfad zurück und sagt es einmal im Log; `.komet toggle hudraster`
schaltet live, `HudBackgroundRaster` in der komet.json (Layout 4) persistent. Zusätzlich
spart die Kompaktansicht den Pool-Walk in `SampleWorld` (GetStats + CalcFragmentation über
alle Mesh-Pools), dessen Zeilen sie gar nicht zeigt. Die `hud`-Anteile in Ruckler-Zeilen
buchen seitdem nur noch die Main-Thread-Anteile — die Worker-Zeit stiehlt keinem Frame etwas.

Neben `Komet.dll` wird `KometBaseline.dll` mit installiert. Die enthält **nur die Messung**
und **keine einzige Optimierung** — dasselbe HUD, aus buchstäblich denselben Quelldateien
(`Measure/`), damit eine Zahl hier und eine Zahl dort dasselbe bedeuten und sich subtrahieren
lassen. Ihre einzigen Harmony-Patches lesen eine Uhr.

```
Mod-Manager: komet AUS  →  Spiel neu starten  →  F7, Zahlen notieren   (Titel: "vanilla")
Mod-Manager: komet AN   →  Spiel neu starten  →  F7, Zahlen vergleichen (Titel: "komet")
```

Der Titel oben im HUD sagt dir, welche gerade misst. Sind **beide** aktiv, hält sich die
Baseline komplett raus (Logzeile statt zweitem Overlay) — eine „Baseline", die neben den
Optimierungen läuft, misst schließlich nicht Vanilla.

Der Unterschied im HUD: die Baseline hat den Abschnitt `── komet ──` nicht, weil
Sichtbarkeits-Sweep, Draw-Range-Merging und Occlusion-Zeit dort schlicht nicht existieren.
Alles darüber — fps, Frame-Zeit, game tick, Render-Stages, Schatten, draw calls, VRAM — ist
identisch erhoben.

---

### Im Spiel

**`F7` schaltet das Performance-HUD oben rechts an und aus.** Das ist der bequeme Weg —
der Chat taugt nicht, um sieben Zeilen im Blick zu behalten.

```
F7               HUD an/aus (in den Tastenbelegungen umlegbar)
.komet          dieselben Zahlen kompakt im Chat (gut zum Weitergeben)
.komet report   ALLES auf einmal ins client-main.log - zum Weitergeben (1.46.0)
.komet hud      HUD umschalten
.komet reset    Zähler zurücksetzen
```

**`.komet report` ist der Befehl zum Weitergeben.** Er schreibt einen einzigen Block ins
`client-main.log`, zwischen `==== komet report ====` und `==== ende ====`: Umgebung (Kerne,
GC-Modus **und** was per `DOTNET_gcServer` angefordert wurde — die beiden können
auseinanderfallen, und dieser Unterschied hat schon einmal zu einer falschen Schlussfolgerung
geführt), alle Einstellungen die vom Standard abweichen, die komplette Frame-Aufteilung und
das vollständige Ruckler-Protokoll. Der Grund für den Befehl: jede Diagnose bisher wurde aus
drei bis vier Einzelbefehlen zusammengesetzt, und der entscheidende war regelmäßig der, den
niemand ausgeführt hatte. Ein Block, der per Konstruktion vollständig ist, kann nicht
halb berichtet werden.

Die Liste der abweichenden Einstellungen wird per Reflection über `KometConfig` gebildet,
nicht von Hand gepflegt — eine Handliste hört still auf, genau die Einstellung abzudecken,
die nach ihr dazukam, und das ist immer die, um die es gerade geht. Der `verify`-Test setzt
dafür jede Property einzeln um und verlangt, dass genau sie im Delta auftaucht.

Das HUD zeigt:

```
komet 1.3.1   Mittel ueber 240 Frames
──────────────────────────────────────────
 fps                101    9,86 ms
 schlechtester            29,60 ms
 game tick         14 %    1,42 ms
── render stages ─────────────────────────
 before             3 %    0,31 ms
 shadow far        14 %    1,35 ms
 shadow near       11 %    1,10 ms
 opaque            43 %    4,20 ms
 oit                6 %    0,62 ms
 ortho (gui)        4 %    0,41 ms
 done               4 %    0,35 ms
 = schatten        25 %    2,45 ms
── komet ────────────────────────────────
 sichtbarkeit      20 %    1,98 ms
 teile getest.   71.367
 draw ranges      3.350  von 10.459 (3,1x)
 chunk upload              0,38 ms  max 2,5
 occlusion                 6,40 ms  worker-thread
── welt ──────────────────────────────────
 draw calls       3.412
 dreiecke     12.400.000  von 41.200.000
 entities            87
 chunks          16.456  warteschl. 12/3
 sichtweite       1.536  blocks
```

Die Render-Stages summieren sich zur Frame-Zeit, weil der Client Game-Tick *und* alle Stages
auf dem Main-Thread fährt. Damit ist direkt ablesbar, wo ein Frame hingeht — insbesondere,
was die Schatten kosten.

`.komet` schreibt seine Ausgabe seit 1.9 zusätzlich ins `client-main.log` — Zahlen aus einer
Session sind damit nachlesbar statt nur im Chat-Fenster sichtbar.

**Punkt, nicht Slash.**

`.komet` ist ein Client-Befehl. `/komet` geht an den Server, wo
dieser Mod (client-only) nicht existiert — die Antwort ist dann „Es gibt keinen solchen Befehl",
was aussieht, als wäre der Mod nicht geladen. Ob er geladen ist, steht in
`VintagestoryData/Logs/client-main.log`: `[komet] enabled: …` pro aktivem Patch.

Die Ausgabe nennt Frame-Zeit, den Anteil des Sichtbarkeits-Sweeps daran, getestete Mesh-Teile
pro Frame, Draw-Ranges vor/nach dem Verschmelzen, Upload-Zeit pro Frame und ob der Upload
überhaupt persistentes Mapping benutzt.

### Vorher/Nachher messen

1. `.debug tickprofiler 5` im Chat, auf `rendOpaque-4` / `rend3D-ret-*` achten.
2. Oder ein MangoHud-Benchmark-Lauf (`mangohud --dlsym`, Frametime-Log) einmal mit und einmal
   ohne die dll in `Mods/`.

### Konfiguration

**Die Datei trägt eine `ConfigVersion`.** Das ist die *Layout*-Version der Config, **nicht** die
Mod-Version — die beiden waren früher dasselbe Feld, und das hieß: jedes Release warf jedem
seine Einstellungen weg, auch die Releases, die an der Config nichts geändert hatten. Gebumpt
wird sie von Hand (`KometConfig.Current`) und nur dann, wenn eine Einstellung dazukommt,
verschwindet oder einen neuen Default bekommt. Passt sie nicht, wird die Datei neben sich
selbst gesichert (`komet.json.<altesLayout>.bak`) und aus den aktuellen Defaults neu erzeugt.
Der Suffix wird dabei auf harmlose Zeichen reduziert — der gespeicherte Wert kommt aus einer
Datei, die der Nutzer editieren kann, und landet in einem Pfad.

Das ist keine Kosmetik, sondern behebt eine echte Falle: `LoadModConfig` liest, was auf der
Platte liegt, und `StoreModConfig` schreibt es unverändert zurück. Einen Default in der Quelle
zu ändern erreicht damit **niemanden, der die Datei schon hat** — ein Fix, ausgeliefert als „der
Default ist jetzt X", bedeutet für jeden bestehenden Spielstand schlicht „unverändert". Genau so
blieb ein Schatten-Fix hier halb angewandt, bis ich in die echte Datei gesehen habe.

`verify` prüft die Regel in beide Richtungen: ältere, neuere und fehlende Version werden neu
erzeugt, die passende Version bleibt unangetastet (sonst würde die Datei bei jedem Start
zerschrieben). Gegengeprüft mit „nie regenerieren" — dann schlägt der Test fehl.

`dist/komet.json` ist die ausgelieferte Default-Config und wird nicht von Hand gepflegt:
`./build.sh config` serialisiert die echte `KometConfig`-Klasse. Eine handgepflegte Datei
dokumentiert spätestens beim ersten geänderten Default eine Mod, die es nicht gibt.

### Version und Buildnummer

Die Mod-Version ist **1.0.0** und steigt nur bei einem echten Release. Sie kann ein Testbuild
nicht identifizieren — ein Dutzend davon teilen sich dieselbe Nummer, und die Frage, die ein
Feld-Log beantworten muss, lautet „welche DLL lief da". Deshalb stempelt jeder Build die
Kompilierminute als `yyMMdd.HHmm` in die `AssemblyInformationalVersion` und zeigt sie überall
neben der Version:

```
komet 1.0.0 (b260830.1917)          HUD-Titel, Report-Kopf, .komet-Chatzeile
```

Der Stempel liegt im Assembly-Attribut statt in einer generierten Quelldatei oder einem
eingecheckten Zähler, weil beide von der DLL abweichen können, die tatsächlich verschickt
wurde — dieser hier wird vom Compiler geschrieben, der das Binary erzeugt hat. `build.sh` liest
die Uhr einmal und gibt denselben Stempel an Mod *und* Baseline, damit ein Vergleich nicht mit
zwei Builds endet, die eine Minute auseinanderliegen. Wer die Sources ohne den Stempel-Target
übersetzt (`verify`, `bench`), bekommt eine Zeile ohne Buildnummer statt einer Exception.



`~/.config/VintagestoryData/ModConfig/komet.json`, wird beim ersten Start angelegt.
Jeder Patch einzeln abschaltbar — wenn irgendwas komisch aussieht, lässt sich damit ohne
Neubau bisektieren.

| Key | Default | |
|---|---|---|
| `FastFrustumCulling` | `true` | Sichtbarkeits-Sweep über die Mesh-Teile |
| `ParallelCulling` | `true` | alle Pools einer Render-Stage in einem parallelen Durchgang cullen |
| `CullingThreads` | `0` | 0 = automatisch (CPU-Kerne minus eins, max. 12) |
| `PoolLevelCulling` | `true` | ganze Pools per Bounding-Box verwerfen |
| `MergeDrawRanges` | `true` | benachbarte Index-Ranges zu einem Draw zusammenfassen |
| `FastOcclusionCulling` | `true` | Raywalk hoisted + Gitter + parallel |
| `OcclusionCullingThreads` | `0` | 0 = automatisch (CPU-Kerne minus zwei, max. 8) |
| `OcclusionMinIntervalMs` | `200` | Streaming-getriggerte Occlusion-Pässe zeitlich begrenzen (effektiv max(Wert, 5× letzte Pass-Dauer) — selbstregulierend auf ~20 % eines Kerns); Kamera-Wechsel über Chunk-Grenzen passieren weiter sofort. 0 = Vanilla |
| `ServerWorldgenThreads` | `4` | Worldgen-Threads des integrierten Servers — maßgeblich, `servermagicnumbers.json` wird dafür ignoriert; `1` = Vanilla |
| `ServerRequestQueueSize` | `4000` | Server-Chunk-Request-Queue (0 = Vanilla 2000) |
| `ServerChunksColumnsPerTick` | `0` | Annahmerate der Server-Lade-Queue (0 = Vanilla 4) |
| `BulkMeshUpload` | `false` | Chunk-Meshes am Stück kopieren — wirkungslos ohne persistentes Mapping |
| `ExperimentalPersistentMapping` | `false` | `allowPStorage` scharf schalten (siehe oben, ungetesteter Engine-Pfad) |
| `MeasureCullTime` | `true` | Sweep messen, damit ms/Frame statt Ereigniszahlen erscheinen (~0,03 ms/Frame) |
| `FixShadowFadeCutoff` | `true` | harte Schattenkante in der Ferne zu einer weichen Verblendung machen |
| `ShadowDistanceMultiplier` | `1.0` | ferne Schatten-Kaskade strecken (klobiger + teurer) |
| `ProfileRenderers` | `true` | Zeit je einzelnem Renderer messen (HUD-Sektion „teuerste renderer") |
| `MeasureGpuTime` | `true` | GPU-Zeit pro Frame messen (`gpu-frame`-Zeile, „GPU-LIMITIERT"-Anzeige) |
| `SunOcclusionQueryInterval` | `4` | Occlusion-Query der Sonne nur jeden N-ten Frame; `1` = Vanilla |
| `StabiliseShadowTexels` | `false` | Schattenprojektion auf ganze Texel rasten, gegen kriechende Kanten beim Gehen |
| `ShadowSkipRedundantLod` | `false` | vereinfachtes Ersatz-Mesh aus den Schatten-Passes lassen, wo der Kamera-Pass es auch nicht zeichnet |
| `ShadowFarUpdateInterval` | `1` | **Untergrenze** für die ferne Kaskade: nie öfter als jeden N-ten Frame. `1` = Vanilla |
| `ShadowFarMaxSkip` | `1` | **Obergrenze**: nie seltener als jeden N-ten Frame |
| `ShadowFarMoveThreshold` | `0.15` | neu zeichnen, sobald die Kamera so viele Blöcke gewandert ist — **überstimmt die Untergrenze**, sonst zeigt sich beim Fliegen die Kante der Shadow-Map |
| `ShadowNearUpdateInterval` | `1` | dasselbe für die nahe Kaskade; > 1 weicht dem fernen Pass automatisch aus |
| `LowLatencyGC` | `true` | `SustainedLowLatency` anfordern — keine blockierenden Gen2-Sammlungen mitten im Frame |
| `AdaptiveChunkInflow` | `true` | Server drosseln, wenn der Client mit dem Vermaschen nicht nachkommt (nur Singleplayer) |
| `InflowLowWaterChunks` | `400` | Rückstand, unter dem gar nicht gebremst wird |
| `InflowHighWaterChunks` | `2000` | Rückstand, ab dem auf 5 % gebremst wird |
| `ReclaimEmptyPools` | `true` | GPU-Puffer leergelaufener Chunk-Mesh-Pools zurückgeben (~10 MB je Pool) |
| `ReclaimEmptyPoolsAfterSeconds` | `20` | wie lange ein Pool leer bleiben muss, bevor sein Speicher freigegeben wird |
| `TesselationNoIdleSleep` | `true` | Tesselation-Thread schläft nur noch bei leerer Queue statt 5 ms nach jedem Tick |
| `TesselationThreadPriority` | `true` | Tesselation-Thread auf AboveNormal |
| `TesselationNeighbourPrefetch` | `true` | Nachbar-Chunks der nächsten Queue-Einträge auf einem Worker vorentpacken |
| `DebugHudVisible` | `false` | HUD schon beim Start anzeigen statt erst per F7 |
| `AdaptiveUploadBudget` | `true` | Upload-Zeit pro Frame deckeln (Rückkopplung brechen) |
| `UploadBudgetTargetMs` | `6.0` | Zielzeit pro Frame für Chunk-Uploads |
| `UploadFramePressure` | `true` | Drossel reagiert auch auf heiße Frames (glthread-Drain), GC-Pausen abgezogen |
| `AnimationLookupWithoutAlloc` | `true` | |
| `AnimationCollisionBoxWithoutAlloc` | `true` | |
| `SkipPerFrameGlErrorCheck` | `false` | siehe oben |
| `StatsLogIntervalSeconds` | `0` | Zähler periodisch ins Log |

Schlägt ein Patch fehl (z. B. nach einem VS-Update), wird das geloggt, die betroffene
Optimierung übersprungen und das Spiel läuft normal weiter.

---

## Sprachen: Logs Englisch, HUD und Chat in der Sprache des Spielers (04.09.)

Eine Trennlinie, die man an jeder Stelle im Code beantworten kann: **wird der Text geloggt, ist
er Englisch.** Report, Ruckler-Zeilen, `.komet toggle`- und `.komet stress`-Ausgaben und jede
`Mod.Logger`-Zeile sind Diagnose-Artefakte — sie landen im `client-main.log`, werden in
Bugreports kopiert und von Leuten gelesen, die den erzeugenden Client nicht haben. **Wird er nur
angezeigt, wird er übersetzt.** Das sind genau zwei Oberflächen: das F7-HUD und die
Chat-Antworten des `.komet`-Befehls (inklusive der Beschreibungen in der Chat-Hilfe).

`Measure/Loc.cs` ist die ganze Mechanik. Jeder Aufruf trägt seinen englischen Text als Argument
mit, nicht nur einen Schlüssel:

```csharp
DebugHud.Row(sb, Loc.Hud("cpu cores"), ...);              // -> komet:hud-cpu-cores
Loc.T("komet:hud-cores-busy", "{0} of {1} cores busy", a, b);
```

Der Fallback ist kein Fehlerfall, sondern der Normalfall in drei Situationen: die Verify-Suite
läuft ohne Spiel und ohne geladene Sprachdatei, KometBaseline zeigt dasselbe HUD ohne eigene
Assets, und eine Sprache kann einen Schlüssel schlicht nicht kennen. In allen dreien erscheint
der englische Text aus der Quelle — nie ein roher Schlüssel, nie eine leere Zeile.
`Loc.Hud` leitet den Schlüssel aus dem Label ab (`"cpu cores"` → `komet:hud-cpu-cores`), damit
eine Zeile nicht von ihrem Eintrag wegdriften kann.

Die Dateien liegen in `assets/komet/lang/{en,de}.json` und wandern per `build.sh release` ins
ZIP. Die Schlüssel tragen ihre Domain selbst (`"komet:hud-fps"`), weil `TranslationService`
einen Schlüssel mit `:` unverändert übernimmt und nur einen ohne die Domain voranstellt.

**Der Eintrag wird unformatiert gelesen (05.09.).** `Lang.GetIfExists(key)` formatiert auch
ohne Argumente, und ein Eintrag mit Platzhalter („warteschlange {0}") wirft dann in der Engine
— die fängt das, loggt einen Error und eine Warning, und das bei jedem Aufruf. Bei einem
HUD-Refresh pro Sekunde standen nach zehn Minuten dreitausend „Translation string format
exception"-Zeilen im Log eines deutschen Clients. `Loc.T(key, english)` fragt seither
`Lang.HasTranslation` (ohne Wildcard-Suche, ohne Log) und `Lang.GetUnformatted`; formatiert
wird nur in der Überladung mit Argumenten, mit der Kultur des Clients. Der Lookup ist
injizierbar (`Loc.Lookup`), und Verify hält mit einer eigenen Tabelle fest: ohne Argumente kommt
der Eintrag samt `{0}` zurück, mit Argumenten die formatierte Übersetzung, für einen fehlenden
Schlüssel der formatierte englische Text.

Verify hält beides zusammen: es liest **die Quelldateien** (nicht das, was ein Lauf zufällig
gedruckt hat), sammelt jedes `Loc.T`/`Loc.Hud` und verlangt, dass `en.json` und `de.json` exakt
diese Schlüsselmenge tragen — ein fehlender zeigt einem deutschen Spieler still Englisch, ein
überzähliger ist eine Übersetzung für Text, den niemand mehr druckt. Dazu vergleicht es die
Platzhalter je Eintrag: eine Übersetzung, der ein `{0}` fehlt, würde sonst mitten im Frame in
`string.Format` werfen.

Nicht übersetzt und mit Absicht: die Bucket-Namen in `WorstFrameTail` (`shadow`, `outside`, …).
Dieselbe Funktion füttert HUD **und** Report, und das Vokabular der Ruckler-Zeilen muss über
beide gleich lauten, sonst reden Log und Bildschirm über verschiedene Dinge.

## Reproduzierbare Releases (05.09.)

Der sha256 auf der ModDB-Seite ist nur dann eine Zusage, wenn ihn jemand nachrechnen kann.
`./build.sh release` erzeugt deshalb aus demselben Commit dieselben Bytes — geprüft mit zwei
Läufen und zusätzlich mit einem frischen Clone an einem anderen Pfad, alle drei identisch.

Drei Dinge mussten dafür stillstehen, und alle drei waren erst nach dem Messen sichtbar:

1. **Der Build-Stempel.** Er war „die Minute, in der gebaut wurde"; im Release-Pfad ist er
   jetzt die Minute des **Commits** (UTC). Für einen Dev-Build bleibt es die Uhr — dort ist
   die Frage ja „welchen Stand habe ich gerade laufen?".
2. **Die Kompilierung.** Roslyn ist von Haus aus deterministisch, aber das Debug-Directory der
   DLL trägt den absoluten Pfad der PDB, und der unterscheidet sich zwischen zwei Checkouts.
   `KometReproducible=true` (nur im Release-Pfad) setzt `DebugType=none`, `PathMap` und
   `ContinuousIntegrationBuild` — der Dev-Build behält seine PDB und damit Zeilennummern im
   Stacktrace. Nebenbefund beim Messen: die SDK hängt die Commit-SHA an die
   `InformationalVersion`, sobald in einem Repo gebaut wird — das erklärte den ersten
   scheinbaren Pfad-Unterschied und ist genau das, was `KometVersion.StampFrom` kürzt.
3. **Das Archiv.** Weder `bsdtar` noch Info-ZIP kommen infrage: beide schreiben ein
   Extended-Timestamp-Extrafeld, das die **ctime** enthält, und die kann kein `touch` setzen —
   zwei Läufe ergaben deshalb zwei Archive, die sich in genau 8 Bytes unterschieden. Das ZIP
   entsteht jetzt mit Pythons `zipfile`: feste Reihenfolge, ein Zeitstempel (der des Commits),
   feste Rechte, keine Extrafelder. Damit fällt auch die `libarchive-tools`-Abhängigkeit im
   Workflow weg.

Ein schmutziges Arbeitsverzeichnis macht die Zusage kaputt, weil der Hash dann zu keinem
Commit gehört — `build.sh` sagt das in dem Fall vor dem Bauen.

**Der Compiler gehört zur Eingabe (05.09.).** Der erste CI-Lauf hat die Lücke sofort gezeigt:
derselbe Commit ergab lokal und auf dem Runner verschiedene DLLs — nicht ein paar Bytes,
sondern verschiedene *Größen* (395.264 gegen 395.776), weil die SDK-Patchstände auseinander
liefen. `global.json` nagelt die Version deshalb fest (`rollForward: disable`), und der
Workflow gibt bewusst keine eigene `dotnet-version` an, damit genau eine Stelle entscheidet.
**Und die zlib gehört auch dazu.** Nach dem Pinning war die DLL bytegleich (beide 395.776,
gleicher Hash), das ZIP aber nicht: jeder Eintrag inhaltlich identisch, jeder Deflate-Strom
anders — Arch liefert `zlib-ng`, der Runner Standard-zlib, und beide erzeugen aus denselben
Daten verschiedene Ströme. Deflate-Ausgabe ist nicht spezifiziert, es gibt also keinen
Parameter, der das geradezieht. Das Archiv wird deshalb **unkomprimiert** gepackt
(`ZIP_STORED`): 179 KB → 447 KB Download, dafür ein Hash, den jeder auf jeder Maschine
nachrechnen kann. Für eine Mod, deren Zweck die Überprüfbarkeit ihrer Aussagen ist, war das
der bessere Tausch.

## Was unter Releases landet (05.09.)

Zwei Sorten, weil sie zwei verschiedene Versprechen sind:

| Auslöser | Tag | Form |
|---|---|---|
| Push auf `main` | `v<version>` | **Entwurf**, jemand schaut drauf und drückt veröffentlichen |
| Push auf `nightly`/`buildtest`, manueller Lauf | `preview-<sha>` | **Prerelease**, sofort sichtbar, nie „latest" |
| Pull Request | — | nichts (ein Fork darf dieses Repo nicht taggen) |

Ein vorhandener Tag wird nie verschoben: eine Version wird einmal veröffentlicht, und ein
Preview-Tag trägt den Commit, gehört also für immer zu genau diesen Bytes und zu dem sha256 in
seinen Release-Notes. Genau deshalb steht in den Notes auch, wie man ihn nachrechnet — der
Build ist reproduzierbar (siehe oben), die Zahl ist also überprüfbar und nicht nur behauptet.

Die Actions laufen auf **node24**: `checkout@v7`, `setup-dotnet@v6`, `upload-artifact@v7`,
`download-artifact@v8`, `action-gh-release@v3`. Die jeweils vorigen Majors hingen noch an
node20; die Versionen sind gegen die `action.yml` der Actions geprüft, nicht geraten.

## Projektstruktur

Ein Ordner ist ein Namespace, ohne Ausnahme: `Culling/` → `Komet.Culling`, `Measure/` →
`Komet.Measure`, die Wurzel → `Komet`. Die Quellordner liegen deshalb seit dem 04.09. direkt
neben `Komet.csproj` und nicht mehr unter `src/` — ein Werkzeug, das den erwarteten Namespace
aus `RootNamespace` plus Pfad ab der Projektdatei rechnet, kam sonst auf `Komet.Src.*` und
„reparierte" das notfalls selbst (einmal geschehen, siehe Fingerprint unten). `Measure/`
kompiliert in vier Projekte hinein und heißt in allen `Komet.Measure`.

```
KometModSystem.cs             Laden, Config, .komet-Command   -> Komet
KometConfig.cs                die Schalter samt Doku-Kommentar
Culling/                      Sichtbarkeit                    -> Komet.Culling
  FastCuller.cs                     Sweep: SoA-Cache, Cull-Loops, Range-Merging
  RayTraversal.cs                   Chunk-Raywalk, Konstanten aus der Schleife gehoben
  FastChunkCuller.cs                Occlusion-Pass: Snapshot, flaches Gitter, parallel
  CullVerifier.cs                   stichprobenweiser Abgleich gegen Vanillas Ergebnis
Runtime/                      Motor unter den Patches         -> Komet.Runtime
  WorkerSet.cs, CpuTopology.cs      Threads und was die CPU wirklich hergibt
  UploadBudget.cs, InflowBrake.cs   Regler für Upload-Zeit und Nachschub pro Frame
  ClientQueues.cs, PoolReclaimer.cs, ArrayPoolByClass.cs, ChunkMarkClock.cs
  AllocSampler.cs, StressTest.cs, WindowPrebuilder.cs
Guard/                        Selbstprüfung                   -> Komet.Guard
  PatchGuard.cs                     fremde Patches auf Komets Methoden, Engine-Drift
  EngineFingerprint.cs            generiert von ./build.sh fingerprint
  TaskCodes.cs                      Paket-Id -> Name, aus Packet_ServerIdEnum gelesen
Patches/                      Harmony-Einsprungpunkte         -> Komet.Patches
Measure/                      Messung, auch von der Baseline  -> Komet.Measure
  FrameStats.cs                     Pro-Frame-Buchführung, exponentiell geglättet
  DebugHud.cs                       das Overlay (Ortho-Stage, Textur alle 250 ms neu)
  MeasurementPatches.cs             reine Zeitmessung
KometBaseline/                     die Vanilla-Messlatte: Measure/ + ModSystem, sonst nichts
verify/                       wendet die echten Patches auf die echten Assemblies an,
                              erzwingt JIT und prüft Verhalten — ohne Spielstart
bench/                        Äquivalenz- und Durchsatzmessung gegen Vanilla
```

`verify` ist wichtig: ein kaputter Transpiler wäre sonst erst beim Mod-Laden aufgefallen.
Es baut den HUD-Text auch ohne GL-Kontext zusammen und prüft ihn auf leere Zeilen und
fehlende Felder — `./build.sh` mit einem beliebigen Argument druckt eine Vorschau.

**Das HUD kann das Spiel nicht abschießen.** `OnRenderFrame` fängt alles ab, loggt einmal und
schaltet sich nach drei Fehlern selbst aus. (Gelernt auf die harte Tour: `GenOrUpdateTextTexture`
dereferenziert die Zieltextur ohne Null-Prüfung, ein `null` beim ersten Aufruf crasht den
Client. Die Textur wird jetzt im Konstruktor angelegt.)
Es prüft unter anderem, dass der Upload-Budget-Transpiler seinen Aufruf wirklich zwischen
das `add` und den `stloc` der richtigen Ausdrucks gesetzt hat, und dass der Regler den Gain
nie über 1,0 hebt.

### Ein Fehlerpfad, der besondere Sorgfalt brauchte

Der Occlusion-Patch löscht zuerst alle Sichtbarkeits-Flags und setzt `centerpos`/`qCount`
weiter, bevor er traversiert. Fällt er *danach* an Vanilla zurück, greift dessen eigener
Early-Out (`centerpos` stimmt ja jetzt) und die komplette Welt bliebe unsichtbar. Der
Fallback für pathologisch verstreute Chunk-Mengen traversiert deshalb aus dem Snapshot
weiter, statt zurückzugeben.

---

## Was ich *nicht* gemacht habe

* **Vulkan-Backend** — im Vorprojekt als nicht machbar belegt, und VS ist ohnehin
  CPU-limitiert im Game-Tick, nicht Grafik-API-limitiert.
* **`ClientAnimator.calculateMatrices`** — der Reset-Loop macht pro animierter Entity pro
  Frame eine `jointsById.ContainsKey`-Abfrage je Joint, obwohl sich die Joint-Menge nie
  ändert. Wäre optimierbar, verlangt aber, die private rekursive Methode zu ersetzen. Nutzen
  im Bereich 1–3 %, Risiko deutlich höher als beim Rest — bewusst ausgelassen.
* **Die GPU-Seite.** Bei 1536 stehen pro Frame zweistellige Millionen Dreiecke an, dazu
  Shadow-Pässe. Falls du nach diesen Patches immer noch GPU-limitiert bist, hilft nur
  Sichtweite, Schattenqualität oder LOD-Bias.
* **Bounding-Boxen pro 64er-Block innerhalb eines Pools.** Gebaut, gemessen, wieder
  ausgebaut: sie verwerfen praktisch nichts (0–4 von 15.283 Teilen). Der Grund ist genau die
  Distanz-Sortierung, auf die ich gesetzt hatte — 64 aufeinanderfolgende Teile sind ein *Ring*
  um den Spieler, und dessen Bounding-Box umspannt den halben Sichtbereich. Räumliche
  Kohärenz innerhalb eines Pools gibt es nur radial, nicht als Box.
* **Die sichtbaren Flächen mit auf den Fenster-Worker.** `CalculateVisibleFaces(_Fluids)`
  hängt nur am Fenster, nicht am Meshing des vorigen Chunks — könnte also überlappen. Ruft
  aber virtuelle `Block`-Methoden auf (auch die fremder Mods) und benutzt `tmpPos` des einen
  Tesselators. Der nächste greifbare Posten am Ladepfad, aber er braucht dieselbe
  In-Game-Validierung wie der Fensterbau. Siehe „Drei Posten am Ladepfad" unten.
* **`OnBeforeFrame` vom Upload-Lock befreien.** Der Upload läuft unter demselben Lock, das der
  Tesselations-Thread für `EnqueueOrMerge` braucht — sieht nach Kontention aus, ist aber keine:
  die Upload-Queue steht bei fünf Einträgen gegen 1585 wartende Tesselationen. Umbau eines
  funktionierenden Systems ohne messbaren Gewinn.
* ~~**Lücken-Merging bei Draw-Ranges.**~~ Bis 1.49 stand hier „den Aufwand nicht wert" —
  begründet mit einer Szene, die damals GPU-gebunden war. Die 1.47/1.48-Reports zeigten das
  Gegenteil (gpu ~2,5 ms von ~13 ms Frame, 15.749 Ranges über 929 Draw-Calls), also ist es
  seit 1.50.0 gebaut: siehe „Lücken-Merging" unten. Die Unterscheidung aus der alten
  Begründung gilt darin unverändert — über *frustum*-verworfene Teile wird verschmolzen (die
  GPU clippt sie, null Fragmente), über *distanz*-verworfene, versteckte, occlusion-verdeckte
  oder freie Bytes nie.

## IDE-Cleanup gegen Harmony-Namen (01.09.)

Harmony bindet Patch-Parameter **per Namen**: `__instance`, `__result`, `__state`,
`___feld`, und die Original-Parameternamen der Engine-Methode in genau deren Schreibweise
(`OnRetesselated`, `Indices`, `index3d`). Ein „Namensregeln anwenden"-Cleanup (ReSharper/
Rider, Roslyn IDE1006) macht daraus `instance`, `onRetesselated`, `index3D` — und dann wirft
`Patch()` beim Start („Parameter "instance" not found"), der `Patch()`-Wrapper loggt einen
Fehler und die Mod läuft an der Stelle **still vanilla**. Am 01.09. hat das ReSharper-Backend
von VS Code den ganzen Baum auf `var` umgestellt und dabei nebenbei in sieben Patch-Dateien
genau diese Namen umbenannt (Edge-Koaleszenz, Schatten-Drossel, Schattenbox-Patches,
Feuerstelle, Fenster-Pipeline, Occlusion, Animation) — verify hat es gefangen, in der
gespielten Welt hätte es einfach nur weniger Mod gegeben.

Deshalb: `.editorconfig` schaltet die Namens-Inspektionen für das Repo ab, jede Patch-Datei
trägt `// ReSharper disable InconsistentNaming`, und wer einen neuen Patch schreibt, prüft
im Zweifel `git diff | grep __instance`. Verify bleibt die letzte Instanz — es wendet jeden
Patch wirklich an.

## Messen statt raten

Ich kann das Spiel hier nicht starten — alle Zahlen oben stammen aus Harnischen gegen die
echten Assemblies, nicht aus deiner Welt. Was bei *dir* wirklich zieht, sagt:

```
.komet            im Chat: alle Zähler, inkl. ob der Bulk-Upload-Pfad überhaupt aktiv ist
.debug tickprofiler 5
```

Interessant sind vor allem:
* `mesh upload … fell back to glBufferSubData` — wenn das die Mehrheit ist, hat dein Treiber
  kein persistentes Mapping und Patch 1 des Upload-Pfads greift nicht.
* `upload throttle gain` — steht der dauerhaft unter 100 %, war der Upload tatsächlich dein
  Flaschenhals.
* `draw ranges … x fewer` — der reale Merge-Faktor in deiner Welt.
* `occlusion pass … peak` — sollte jetzt einstellig statt dreistellig sein.

## Feldreport schwache Hardware: i3-7100U, HD 620, „Optimum"-Build (03.09.)

Ein Tester-Log (2 Kerne / 4 Threads, Intel HD 620, 12 GB, KDE neon; Spiel als modifizierter
Engine-Build „Optimum v0.3.14", 123 Mods, fps-limit 30) hat sieben Dinge gezeigt, von denen
fünf in der Mod lagen. Reihenfolge nach Schwere, nicht nach Auffälligkeit.

**1. `AmbiguousMatchException` in `MeasurementPatches.Apply` — Root Cause und Teil-Anwendung.**
`AccessTools.Method(typeof(ChunkTesselator), "populateTesselatedChunkPart")` ohne Signatur
löst über `Type.GetMethod(name)` auf, und das wirft, sobald eine zweite Überladung existiert —
der Optimum-Build hat eine. Schlimmer als der Fehler war sein Ort: mitten in der Gruppe. Die
Klammern davor (Stages, Tick, Upload, Tesselation, Nachbarn, Relight) waren angewendet, die
danach (JSON-Alloc, Netz-Alloc, **Swap-Timing**) fehlten still, und das Log sagte „could not
enable … running without it" — beides falsch. Jetzt: die vier Kern-Klammern sind Pflicht und
werfen; jede Attributions-Klammer wird einzeln angewendet (`Optional(name, …)`), ein Ausfall
wird **mit Namen** geloggt und steht im Report (`messung ohne: …`). Für die Klone-Klammer
werden **alle** Überladungen des Namens gepatcht (`PatchEveryOverload`), Prefix/Postfix sind
verschachtelungssicher (thread-statische Tiefe, nur der äußerste Aufruf bucht) — welche
Überladung die Engine ruft und ob eine die andere wickelt, ist ihr Ding. Verify stellt einen
Typ mit zwei Überladungen nach, prüft dass die reine Namenssuche wirft, und dass der
verschachtelte Aufruf einmal bucht.

**2. `gpu 173.73 ms` bei 33,9 ms Frame — der GPU-Timer stand seit dem zweiten Frame.**
Der Ring aus vier `GL_TIME_ELAPSED`-Queries verweigerte einen `BeginQuery` auf einem Slot,
dessen Ergebnis noch nicht gelesen war. Gelesen wurde zweimal je Sekunde, verbraucht ein Slot je
Frame: nach vier Frames war der Ring voll, und der End-Handler — die einzige Stelle, an der die
Lese-Uhr lief — kehrte ohne aktive Query sofort zurück. Der einzige Wert, der je gelesen wurde,
war der eines Join-Frames, für den Rest der Session eingefroren. Das erklärt rückwirkend auch
das „byte-identisch über und unter Wasser" (als Glättungsträgheit gedeutet) und den
„GPU-gebunden"-Befund eines früheren RX-570-Reports — beide waren dieselbe eine Zahl. Jetzt:
Slots werden überschrieben (ungelesenes Ergebnis verfällt), die Regel lebt GL-frei in
`GpuFrameTimer.QueryRing`, Verify treibt 3000 Frames hindurch (jeder Frame beginnt eine Query,
~200 Lesungen, nie der gerade beendete oder der nächste Slot) und simuliert die alte Regel als
Gegenprobe (steht nach vier Frames). Der Report trägt jetzt `gpu … (N proben)`.

**3. `gc 0.0 ms/s, 0 MB/s alloc, cpu 0.0, 0 chunks/s` neben Rucklern mit 40 ms GC-Pause.**
Alle Sekundenraten wurden in `DebugHud.SampleWorld()` gefaltet — also nur bei sichtbarem
Overlay. Ein `.komet report` mit HUD aus (so kommt jeder Feldreport) druckte Nullen. Jetzt
faltet `FrameStats.Advance` an der Frame-Grenze alle 0,5 s (`SampleGc` + `PeriodicSample`,
worauf `TesselationStats.Sample` hängt), Render-Thread, HUD egal. Verify: 3 s synthetische
Frames ohne HUD → ~6 Faltungen, keine innerhalb des Intervalls.

**4. Thread-Überbelegung: 4 Cull + 3 Occlusion + 5 Worldgen auf 4 Hardware-Threads.**
`WorkerSet.AutoThreads` riet „über vier Hardware-Threads zweifach SMT" — richtig für 6c/12t,
exakt falsch für den 2c/4t-Laptop, der als vier Kerne galt. Dazu `ServerWorldgenThreads = 6`
ohne Blick auf die Maschine (`additionalWorldGenThreadsCount = 5`). Zwölf beschäftigte Threads
auf vier Hardware-Threads, der Render-Thread und der Collector hinten in der Warteschlange —
die 992-ms-„gen1"-Pause im Log ist Summe aller Pausen eines 2,5-s-Ticks, aber jede einzelne
davon wartet darauf, dass alle diese Threads einen Kern bekommen. Jetzt `CpuTopology`: Linux
liest `physical_package_id`/`core_id` aus sysfs, Windows `GetLogicalProcessorInformation`,
macOS `hw.physicalcpu`; die Rate-Regel bleibt Fallback, der Report nennt die Quelle
(`kerne: 2 physisch von 4 logischen (sysfs)`). Budgets: Cull-Helfer = physisch − 1, Occlusion =
physisch − 2, **null ist erlaubt** (auf zwei Kernen läuft der Occlusion-Walk inline auf seinem
eigenen Thread statt als dritter Busy-Thread); Worldgen = min(konfiguriert, Hardware-Threads
− 2) — der 12-Thread-Desktop behält 6, der Laptop bekommt 2, das Log nennt die Kappe. Verify:
sysfs-Parser mit Laptop-, Desktop-, Zwei-Sockel- und Datei-fehlt-Topologie, alle Budgets.

**5. `shadow map framebuffers rebuilt at 5120px` auf einer HD 620 mit Schatten unter Maximum.**
`ShadowMapExtraQuality = 1` existiert, weil der Regler bei 4 (6144 px) endet. Unterhalb kann der
Spieler selbst höher stellen und hat es nicht — auf einer iGPU aus gutem Grund. Die Stufe griff
trotzdem, und der erzwungene Framebuffer-Rebuild beim Join (der **alle** Framebuffer neu baut)
lief auch bei Schatten *aus*. Jetzt `StepsFor(engineSteps, extra)`: Extra-Stufen nur ab
Engine-Stufe 6 (= Qualität 4); darunter Vanilla-Größe, und `TryForceRebuild` steigt mit
Log-Zeile aus statt zu rebuilden. Stellt der Spieler mitten in der Session auf 4, baut die
Engine selbst neu und die Regel greift. Konfig-Kommentar entsprechend; Verify pinnt die Regel.

**6. `ruckler: 5638.5 ms … draussen 5627.3` acht Sekunden nach „Client pause state is now on".**
Das Pausenmenü stand offen. `HitchLog.NotePaused` (vor der Kamera-Probe an jeder Frame-Grenze,
aus `capi.IsGamePaused`) verwirft einen anstehenden Ruckler, dessen Frame pausiert begann oder
endete; Summary und Hitch-Report nennen die Zahl (`N im pausenmenue verworfen`). Verify: 5-s-
Pausenframe und der Schließ-Frame werden verworfen, ein echter Stall danach bucht.

**7. `thread priority: True` unter Linux.** `Thread.Priority` wird auf Linux gespeichert und
nie angewendet (CoreCLR-PAL; für die Cull-Worker gemessen), und ein niedrigerer Nice-Wert
braucht Rechte, die das Spiel nicht hat. Die Log-Zeile behauptete einen Hebel, den es nicht
gibt; jetzt „not available on Linux", und die Patch-Hälfte wird dort gar nicht erst installiert.

**Nicht von der Mod, und deshalb nicht angefasst:** der 3,3-s-Frame bei 208 s (`tick 2561.6`,
gc 992,8 summiert) — ein Tick-Listener, den dieser Build (Stand 6fda7bd) noch nicht benennen
konnte; der `TickProfiler` dieses Arbeitsstands nennt ihn im nächsten Log. Die `Before-ree`-
Bursts von 70–148 ms (Entity-Before-Stage, „enttess" nur 18 ms davon) — dafür sind
`EntityAnimPatches` (Attribution + Animations-LOD) und `EntityLoadPatches` (Lade-Budget) im
selben Arbeitsstand. `DarkVision`-Shaderpatch-Fehler und die 724 fehlenden Texturen/Shapes
sind Mod-Konflikte anderer Mods. Optimums Threadpool-Kappe (`worker=10`) berührt Komet nicht:
Cull, Occlusion, Prefetch und Prebuild laufen auf eigenen Threads.

## Patch-Wächter: wer sonst noch auf Komets Methoden sitzt (03.09.)

Zwei Arten, wie ein Komet-Patch aufhören kann zu bedeuten, was er bedeutet. **Eine andere
Harmony-Mod** patcht dieselbe Engine-Methode: ein fremder Transpiler schreibt die IL um, die
Komets Transpiler erwartet (und Harmony führt bei *jedem* Patch-Vorgang auf der Methode alle
Transpiler neu aus — Komets läuft dann im Patch-Aufruf der anderen Mod und wirft dort, wenn die
Form nicht mehr passt); ein fremder Prefix, der `bool` zurückgibt, kann das Original abbrechen,
das Komets Prefix/Postfix klammern; oder eine Mod patcht direkt Komet-Code. **Ein Fork-Client**
(der „Optimum"-Build des Feldreports) patcht gar nicht, sondern liefert geänderte Assemblies —
und Komets 1:1-Transkriptionen (Task-Drain, Entity-Before-Schleife, Minimap-Upload, Entity-Sync)
ersetzen dort still, was der Fork in derselben Methode geändert hat. Keine Exception, keine
Log-Zeile.

`Guard/PatchGuard.cs` beantwortet beides, ohne Verhalten zu ändern — eine Kollision wird gemeldet,
nie „aufgelöst", denn wer gewinnen soll, ist nicht Sache dieser Mod.

**Optimum und OptiTime bekommen ein Popup (05.09.).** Beide ersetzen denselben Engine-Code wie
Komets Transkriptionen, keine Seite weiß von der anderen — bisher fand man das per Bisect.
`Guard/ForeignClient.cs` erkennt Optimum am Marker-Typ `Optimum.OptimumInfo` in VintagestoryLib
(Version aus dessen Konstante; Fallback: der Versionsstring „… + Optimum v0.3.14"), OptiTime an
der modid `optitime`. Gesucht wird beim Client-Start, die Log-Zeile kommt sofort; beim
Weltbeitritt folgen 1,5 s nach `LevelFinalize` (dann ist der Ladebildschirm weg) ein Dialog
(`ForeignClientDialog`, Layout wie der `GuiDialogConfirm` der Engine) und dieselbe Zeile im
Chat, und der Report nennt den Client in seinem Kopf. Komet bleibt an — wer gewinnen soll,
entscheidet der Spieler. Ein Lookup, der wirft, ist kein Fund und kein Absturz. Verify treibt die
drei Erkennungswege ohne Spiel (injizierte Typ-, Versions- und Mod-Lookups) und prüft, dass jeder
Text den Fund nennt.

**Harmony-Kollisionen** kommen aus Harmonys eigenem Register (`Harmony.GetAllPatchedMethods` +
`GetPatchInfo`): jede gepatchte Methode im Prozess mit Besitzern, Art und Priorität. Gemeldet
wird jeder fremde Patch auf einer Methode, die Komet patcht, und jeder Patch auf Komet-Code.
Stufen: **hoch** = fremder Transpiler neben Komets Transpiler, fremder abbrechender Prefix neben
Komets abbrechendem Prefix (die Zeile sagt, wer zuerst läuft), Patch auf Komet-Code; **mittel** =
abbrechender Prefix auf einer nur gemessenen Methode (die Messung bucht dann einen übersprungenen
Aufruf), fremder Transpiler unter Komets Prefix/Postfix; **info** = nicht abbrechende Prefixe,
Postfixe, Finalizer.

**Wo Komet die Methode ersetzt (04.09.).** Komets Transkriptionen sind abbrechende Prefixe — der
Task-Drain, die Entity-Before-Schleife, die Entity-Paket-Handler, die PhysicsManager-Regeln, der
Minimap-Upload. Auf so einer Methode gilt Vanilla-Harmony-Semantik: der fremde **Transpiler**
läuft nie (das Original läuft nicht), ein fremder **Prefix**, der hinter Komets einsortiert ist,
wird gar nicht mehr aufgerufen, und ein fremder **Postfix** läuft zwar, sieht aber das Ergebnis
der Transkription statt das des Originals. Das ergibt die Feldmeldung „seit dieser Mod sind
Tiere unsichtbar" ohne eine einzige Exception. Der Guard stuft diese drei Fälle deshalb als
**hoch/hoch/mittel** ein und schreibt den Grund in die Zeile (`komets prefix ersetzt das
original, diese IL-umschreibung laeuft nie`), statt sie wie früher als Info abzutun. Geprüft bei `LevelFinalize` (alle Mods sind dann gestartet) und alle 10 s
danach, weil Mods auch spät patchen; jeder Fund wird einmal als Warnung geloggt
(`patch-kollision HOCH: Typ.Methode - 'modid' transpiler (prio 400) neben komet transpiler: …`),
bleibt im Report (`patch-kollisionen: N (x hoch, y mittel, z info)` plus eine Zeile je Fund)
und in `.komet conflicts`. Komets eigene Ids sind `komet`, `komet.server`, `komet.verify`.

**Engine-Fingerabdruck.** `./build.sh fingerprint` lässt die Verify-Suite laufen (die wendet
jeden Patch an) und hasht danach jede gepatchte Engine-Methode in `Guard/EngineFingerprint.cs`:
FNV-1a über Opcodes plus *aufgelöste* Operanden (Member-Namen statt Metadaten-Tokens,
Sprungziele als Offsets, Literale als Text), damit ein Neubau derselben Quelle gleich hasht;
dazu Spielversion und Modul-Ids der betroffenen Assemblies. Harmony patcht auf Native-Ebene und
lässt die IL unberührt, also liest der Hash auf einer gepatchten Methode dasselbe wie auf einer
frischen. Beim Weltstart vergleicht `PatchGuard.CheckEngine` erst die Modul-Ids (die
Ganz-Build-Antwort), dann jede gepatchte Methode, die die Tabelle kennt. Ergebnis als eine
Zeile im Log und Report: `engine: v1.22.7 (Stable) - assemblies wie verifiziert (1.22.7), alle 78
gepatchten methoden unveraendert`, oder bei einem Fork `… VintagestoryLib weicht vom
verifizierten build ab; 3 von 78 gepatchten methoden VERAENDERT: A, B, C - komets transpiler und
1:1-transkriptionen dort laufen gegen fremden code`. Weicht nur die Assembly ab, aber keine
Methode, steht das ebenso da — dann treffen Komets Patches bekannten Code, und der Report des
Testers ist trotz Fork verwertbar.

Verify enthält den Vergleich als letzten Check: jede von der Suite gepatchte Engine-Methode muss
in der Tabelle stehen und unverändert sein, sonst „run ./build.sh fingerprint" — nach einem
Spiel-Update ist das der eine Handgriff. Dazu die Live-Prüfung selbst (keine Abweichung gegen
die Installation, und eine mutierte Tabellenzeile wird beim Namen genannt) und der
Kollisionsscanner mit drei Fremdpatches aus einer zweiten Harmony-Instanz: abbrechender Prefix
auf einer gemessenen Methode (mittel, „laeuft VOR"), Transpiler auf `window_RenderFrame` (hoch),
Postfix auf `WorkerSet.AutoThreads` (hoch, Komet-Code); jeder Fund einmal geloggt, ein Rescan
ohne Änderung meldet nichts, nach dem Unpatch ist die Liste leer. Nebenbefund des Tests:
Komet hat auf `TriggerRenderStage` selbst einen abbrechenden Prefix (Shadow-Throttle), ein
fremder abbrechender Prefix dort ist deshalb zu Recht „hoch".

## Log 03.09. (eigener Rechner): der Rest bekommt Namen, und der GC seine Überlebenden

Der erste Log nach dem Feldreport, noch mit dem Build vom Vorabend (`b260902.1910`, also ohne
die Fixes desselben Tages — `gpu 1,32 ms` ist dort noch der eingefrorene Wert), Join-Flut mit
721 Chunks/s empfangen, 204/s tesseliert, Warteschlange 4.500: **384 von 402 Rucklern tragen
eine GC-Pause**, 193 ms/s Pausenzeit, 27 gen0/s, längste gen0/gen1-Pause 33 ms. 216 MB/s
Allokation, davon `server 69`, `tess 35`, `netz 17`, `main 11`, `prefetch 4` — und **79 MB/s
„rest = ungemessen"**. Die anderen Zeilen des Logs sind Einzelfälle: `readpacket6 693 ms` ist
`LevelFinalize` beim Beitritt; die 31 ms für `trader-female-clothing-temperate` sind die
Hauptthread-Hälfte einer Händler-Tesselation mit Kleidung, die das Entity-Budget nicht teilen
kann (mindestens eine je Frame); `WorldMapManager.OnClientTick 10,5 ms` einmal.

Zwei Messungen, damit der nächste Log die GC-Frage beantwortet statt sie zu stellen:

**`ClientAllocPatches`** — die acht Engine-Worker-Threads des Clients (`tesselateterrain`,
`networkproc`, `relight`, `compresschunks`, `chunkvis`, `chunkculling`, `blockticking`,
`asyncparticles`) bekommen je eine Klammer um `ClientThread.Update`, benannt nach dem
Thread-Namen — disjunkt und vollständig für alles, was diese Threads tun. Dazu wird jede an
`TyronThreadPool.QueueTask(Action, string)` übergebene Aktion gewickelt, sodass ihre Bytes auf
dem Aufrufer-Namen landen, egal welcher Pool-Thread sie ausführt (die `Func<Task>`-Variante
bleibt draußen: nach einem `await` läuft die Fortsetzung womöglich auf einem anderen Thread, und
ein Per-Thread-Zähler kann ihr nicht folgen). Die bestehenden Klammern (Meshing in
`TesselateChunk`, Netzwerk-Systemtick) bleiben und speisen weiter die `laden:`-Zeile; das
Thread-`tess` und `netz` hier enthalten sie. Report: `alloc-quellen: main, client-threads,
threadpool, prefetch, server, rest` plus `alloc client-threads: tess 35, netz 17, compress …
MB/s | threadpool N MB/s: enttess …`. `.komet toggle clientalloc`, Konfig
`ClientAllocAttribution` (Layout 10).

**GC-Überlebende.** Die Pause wird für das bezahlt, was der Collector *behält*, nicht für
das, was alloziert wurde: Müll ist billig, Überlebende werden markiert und nach gen1 kopiert,
und die Karten der alten Generation, die auf sie zeigen, werden gescannt. Gestreamte Weltdaten
überleben per Definition. `FrameStats` liest jetzt in jedem Frame, der eine Sammlungsgrenze
überschritten hat, `GC.GetGCMemoryInfo(Ephemeral)` — bei 27/s und 90 fps sieht das fast jede
Sammlung genau einmal — und faltet `befoerdert MB/s`, `MB je sammlung`, Generation und Pause der
letzten Sammlung, Heap-Größe. Report-Zeile `gc-details: gen1 x/s, befoerdert N MB/s = M MB je
sammlung (bis zu P % der allokation ueberlebt), letzte gen1 9,4 ms pause, heap 2.100 MB`.
gen0- und gen1-Beförderung zählen beide, ein zweimal befördertes Objekt also doppelt — der
Anteil ist eine Obergrenze. Liegt er nahe der Allokationsrate, sind es Weltdaten, die leben
müssen, und nur weniger laden je Sekunde hilft (die Zuflussbremse stand in diesem Log bereits
auf 3 %); liegt er weit darunter, ist es Müll, und die `alloc-quellen`-Zeilen sagen wessen.

## GPU-Spanne ist nicht GPU-Last, eine Stichprobe über alle Threads, und SheyderMod (03.09., abends)

### „Meine GPU ist nach dem letzten Patch extrem ausgelastet"

War sie vorher auch. Alter und neuer Report haben dieselbe Framezeit (10,86 gegen 10,93 ms),
dieselbe Swap-Wartezeit (0,51 ms), dieselbe Schattenkarte (7168 px), dieselben Anzeigewerte
(Vsync aus, Limit 240). Der einzige Unterschied war die gpu-Zahl: 1,32 ms gegen 10,20 ms. Die
1,32 ms stammten vom eingefrorenen Timer der Vorversion (siehe oben), gelesen in der ersten
halben Sekunde im Ladebildschirm. Die 10,20 ms sind das reale Bild, und das war vorher nur
nicht sichtbar.

Und selbst diese Zahl heißt nicht „die GPU rechnet 10 ms". `GL_TIME_ELAPSED` misst die Spanne
zwischen erstem und letztem Befehl des Frames auf der GPU, **inklusive der Lücken, in denen
sie auf den Hauptthread wartet**. Ein CPU-limitierter Frame, der über 10 ms Befehle
nachschiebt, liest sich genauso wie ein GPU-limitierter, der 10 ms rechnet. Der Hauptthread
war mit rund 9,7 ms seinerseits fast voll; welche Seite die Wand ist, sagt die Spanne nicht.

Was es sagt, ist die Auslastung des Treibers. Auf Linux veröffentlicht amdgpu sie in sysfs
(`/sys/class/drm/cardN/device/gpu_busy_percent`), ein Integer, vom Treiber gepflegt. Neu in
`Measure/GpuBusy.cs`: zweimal die Sekunde gelesen (Mikrosekunden, kein GL-Aufruf, kein
Treiber-Sync), erste Karte mit der Datei (Connector-Einträge wie `card1-DP-1` werden
übersprungen), nach drei unlesbaren Werten schaltet sich die Zeile ab statt zu lügen. HUD-Zeile
`gpu-last 93 %`, Report `gpu 10,20 ms (153 proben, auslastung 93 % laut amdgpu)`. Die Marke
„GPU-LIMITIERT" entscheidet ab jetzt die Auslastung (ab 90 %), wo es sie gibt; ohne Zahl bleibt
die alte Spannen-Regel als das schwächere Signal, das sie ist. Intel und NVIDIA liefern in
sysfs nichts Vergleichbares, Windows hat keine Datei; die Zeile fehlt dann, statt zu raten.

Zur Einordnung der Last selbst: ohne Vsync und mit Limit 240 rendert das Spiel so schnell es
kann. Bei 91 fps ist die GPU dann in jedem Build nahe Vollast. Komets eigener Anteil sind die
Standardwerte 255 Blocks Schattenweite, eine Extrastufe Schattenkarte und LOD3 im Schattenpass;
dazu kommt SheyderMod mit Deferred Rendering, GTAO, SSR und volumetrischem Nebel, alles
GPU-Arbeit.

### SheyderMod: was es ist und wo es Komet berührt

Kein „einfacher Shader-Mod". Die Dekompilierung (110 Dateien) zeigt eine eigene
Blockbeleuchtung (Sonnen-Bake und Laternen-Rebake, gerechnet auf der **Relight-Thread des
Clients** mit 25 ms Budget je Tick, Lichtvolumen im eigenen Speicher, Überschreiben von
`currentChunkRgbsExt` im Postfix auf `ChunkTesselator.BeginProcessChunk`), Deferred Rendering,
GTAO, Bloom, Lens Flare, mechanische Schatten in den Shadow-Stages, Specular-Atlas, SSR für
Wasser, volumetrischer Nebel. 19 gepatchte Engine-Methoden.

Geprüft, Methode für Methode gegen Komets Patchziele:

- **Harmony-Überlappung** gibt es genau eine: `JsonTesselator.AddJsonModelDataToMesh`.
  SheyderMod beleuchtet dort in Prefix und Postfix breite Shapes nach, Komet hat dort nur die
  Allokationsklammer. Kein Abbruch, kein Transpiler, nichts zu entscheiden.
- **BeginProcessChunk-Postfix gegen den Fenster-Vorbau:** Komets Prefetch tauscht in
  `BuildExtendedChunkData` das vorgebaute Fenster ein, SheyderMods Postfix überschreibt danach
  die Lichtwerte im Feld, das der Tesselator hält. Reihenfolge stimmt, beide auf dem
  Tesselations-Thread, kein zweiter Tesselator.
- **EntityShapeRenderer.BeforeRender-Postfix gegen die Entity-Transkription:** Komets Schleife
  ruft `renderer.BeforeRender(dt)` für sichtbare Entities, das ist die gepatchte Methode, der
  Postfix läuft. Das Anim-LOD betrifft nur `AnimManager.OnClientFrame`.
- **Mechanische Schatten in ShadowFar:** Komets Drossel überspringt die Stage samt Renderer;
  die zurückgehaltene Karte enthält den Beitrag des letzten echten Renders. Bei bis zu vier
  Frames Alter bei 90 fps nicht sichtbar.
- **Relight-Thread:** SheyderMods Bake läuft im Postfix auf
  `ClientSystemRelight.OnSeperateThreadGameTick`, also **innerhalb** von Komets Thread-Klammer.
  Sein Allokationsanteil steht in der Zeile `alloc client-threads` unter `relight`; im Report
  vom 03.09. lag er unter 0,5 MB/s.
- SheyderMod hat keine eigenen Threads (`new Thread`, `Task.Run`, ThreadPool: keine Treffer).

Zwei Dinge waren trotzdem falsch, beide auf Komets Seite:

1. **Der Patch-Wächter hat die Messklammer als Kollision gewarnt.** Zwei Zeilen
   `patch-kollision INFO` im Warning-Log, dazu „Captured 2 issues during startup". Ein
   fremder Prefix ohne Abbruch neben einer reinen Zeitmessung ist keine Kollision im Sinn der
   Frage „überschreibt jemand Komets Funktion". Der Wächter erkennt jetzt, wenn alles, was
   Komet auf der Methode hat, aus `Measure/MeasurementPatches` stammt (`Finding.MeasurementOnly`),
   schreibt dann „neben komets messklammer (prefix+postfix): … komet misst hier nur, nichts zu
   entscheiden", und **Info-Befunde gehen ins Notification-Log**, nur Mittel und Hoch bleiben
   Warnungen. Die Swap-Transpiler-Klammer zählt als Messklammer, `SunRelightChunk` mit dem
   Postfix des Vorbaus nicht; beides im Harness festgenagelt.
2. **Die Klammern schlossen vor dem fremden Postfix.** Bei gleicher Priorität läuft der
   Postfix der früher registrierten Mod zuerst, also Komets; SheyderMods Nachbeleuchtung lag
   damit außerhalb der `shapes`-Klammer und landete im `rest`. Alle Mess-Prefixe stehen jetzt
   auf `Priority.First`, alle Mess-Postfixe auf `Priority.Last`. Harmony sortiert Prefixe UND
   Postfixe absteigend nach Priorität, also läuft First als erster Prefix und Last als letzter
   Postfix; der Harness prüft die Reihenfolge gegen die echte Harmony und als Gegenprobe, dass
   gleiche Priorität die äußere Reihenfolge nicht liefert. Komets eigene Patches auf gemessenen
   Methoden (Vorbau, Prio-Upload) lagen schon auf Low. Nebeneffekt: ein fremder High-Prefix
   läuft jetzt NACH Komets Mess-Prefix, was der Wächter entsprechend meldet.

### Stichprobe statt Klammer: `alloc-stichprobe`

Nach der Klammer um jeden Engine-Thread blieben im Report 46 MB/s „rest = ungemessen". Wer
das ist, sagt keine Klammer, denn eine Klammer braucht ein Ziel. Die Runtime selbst zählt
aber mit: mit dem GC-Keyword auf Verbose feuert der CLR etwa alle 100 KB Allokation ein
`GCAllocationTick`-Ereignis mit Größe, Typ des Objekts, das die Schwelle überschritten hat,
und OS-Thread-Id. `Runtime/AllocSampler.cs` ist ein prozessinterner `EventListener` darauf: kein
Patch, keine Klammer, keine Engine-Methode berührt. Bei 200 MB/s sind das zweitausend
Ereignisse die Sekunde, je ein Dictionary-Zugriff auf dem Dispatch-Thread der Runtime.

Die Thread-Id wird zum Namen über das, was das OS weiß: Linux `/proc/self/task/tid/comm`
(gefüllt aus `Thread.Name`, auf 15 Zeichen gekürzt, deshalb `tesselateterrai`), Windows
`GetThreadDescription`. Bekannte Engine-Namen bekommen die Labels der anderen Zeilen
(`tess`, `netz`, `chunkdb`), Komets Worker verlieren ihre Nummer (`komet-cull`), der
Hauptthread heißt `main` über seine beim Start gemerkte Id. Ein Thread, der stirbt, bevor
seine Ticks verarbeitet sind, wird als `#tid` gebucht; das kam im Harness prompt vor
(Standalone-Probe: die Ereignisse eines 50-ms-Threads kamen nach seinem Ende an) und ist bei
den langlebigen Engine-Threads kein Thema.

Report-Zeile: `alloc-stichprobe (N proben a ~100 KB): threads main 14, tess 27, netz 21,
chunkdb 26, … MB/s | typen Int32[] 58, Byte[] 41, MeshData 6 … MB/s`. Es ist eine
Stichprobe, und die Zeile sagt es: die Thread-Summen treffen die Allokationsrate im Rahmen
des Sampling-Rauschens, die Typ-Verteilung ist dieselbe 100-KB-Lotterie. Genau genug für
„wessen Müll ist das, und was für Arrays", was die Frage ist. Config `AllocSampling`
(Layout 11), Toggle `allocsample`; der Sampler bucht seinen eigenen Dispatch-Thread ehrlich
als `eventpipe`.

### Paket 58 heißt ExchangeBlock, und wer es schickt, sagt jetzt der Server

`hauptthread-tasks: readpacket58 0,01 ms (638.134x)`: 7.000 Pakete die Sekunde beim
Streamen. Die Id stand nur in der Engine (`Packet_ServerIdEnum`); `Guard/TaskCodes.cs` liest
die Tabelle einmal per Reflection, und der Drain bucht ab jetzt `readpacket58=ExchangeBlock`
und `readpacket6=LevelFinalize`, in Ruckler-Zeile und Report gleich.

Was das kostet: jedes Paket ein Hauptthread-Task, ein Blockschreiben, eine Dirty-Markierung.
Die Retess-Quellen desselben Reports: `MainThreadTaskPatches.RunTasks 32 %` aller
Dirty-Markierungen, dazu `BlockAccessorRelaxed.ExchangeBlock 9 %`; ein frisch gemeshter
Chunk, dem danach seine Blöcke ausgetauscht werden, wird ein zweites Mal tesseliert.
Bulk-Accessoren sind es nicht (deren Commit geht über `SendBlockUpdateBulk` als Sammelpaket);
Einzelpakete kommen aus `world.BlockAccessor.ExchangeBlock` direkt. Kandidaten im
Survival-Code: `BlockShapeFromAttributes.OnServerGameTick("melt")` (Schnee, der von Dächern
schmilzt), Farmland, Crops, Bienenstöcke, Kohlenmeiler. Statt zu raten:
`Patches/PacketSourcePatches.cs`, ein Prefix auf `ServerMain.SendSetBlock` (dem einen
Trichter für Exchange- und Set-Einzelpakete), zählt beide Sorten und nimmt jeden 16. Aufruf
als Kandidaten für einen Stack-Walk bis zum ersten Frame außerhalb der Accessor-Leitung,
gedeckelt auf 25 Captures die Sekunde. Report: `block-pakete (server): exchange 638.134,
set 12 seit reset = 7.001/s | quellen: BlockShapeFromAttributes.OnServerGameTick 91 %, …`.
Ob sich ein Sammelpaket oder ein serverseitiges Zusammenfassen lohnt, entscheidet dieses
Ranking im nächsten Report; die Exchange-Semantik (Block-Entity bleibt) verbietet ein
Umbiegen auf das vorhandene SetBlocks-Sammelpaket.

### Upload 12,7 ms bei 6 ms Budget: neue GL-Pools werden gezählt

Die Ruckler-Liste hatte mehrmals `before 13,5 | upload 12,7` und `upload 9,7`, gegen ein
Upload-Ziel von 6 ms. Ein Vertex-Budget kann eine Kostenart nicht sehen: `MeshDataPoolManager.AddModel`
legt, wenn kein Pool Platz hat, per `MeshDataPool.AllocateNewPool` einen neuen an, und das
ist `AllocateEmptyMesh` mit GL-Puffern von Dutzenden Megabyte, mitten im Upload-Drain. Neue
optionale Messklammer in `Measure/MeasurementPatches.cs` (Zeit je Anlage, Anzahl je Frame),
in der Ruckler-Zeile `upload 12,7 (davon 1 neue pools 9,8)` sobald es ein echter Anteil ist,
im Report `mesh-pools angelegt: N seit reset, X ms gesamt, laengste Y ms`. Erst wenn der
nächste Report sagt, wie oft und wie teuer, lohnt eine Antwort (Pools vorab anlegen, kleinere
Pools, Anlage aus dem Drain heraus).

### Verifiziert

Sechs neue Checks: Klammer-Reihenfolge gegen echte Harmony samt Gegenprobe; Paketnamen aus der
Engine-Tabelle und Buchung unter dem beschriebenen Namen; sysfs-Leser (Parsen, Kartenwahl,
Connector-Ausschluss, Abschalten nach drei Fehlern, LIMITIERT-Regel mit und ohne Zahl);
Allokations-Sampler Ende-zu-Ende (48 MB auf einem Thread `kv-alloc`, gebucht nach Thread und
als `Byte[]`); Pool-Anlage bis in die Ruckler-Zeile; Paketquellen-Sampler mit Ranking,
Auswahlregel und Capture-Deckel. Der Patch-Wächter-Check prüft zusätzlich den
Messklammer-Fall (Info, Notification, nicht Warning). Fingerabdruck auf 82 Methoden.

## GPU-Zeit je Render-Stage, und Ortho-Ruckler nennen ihren Dialog (03.09., spät)

### „Mein GPU-Frame springt über 95 %"

Der Report dazu kam noch vom Build 0053 (Kopfzeile), also ohne `gpu-last`. Die 95 % sind
Spanne durch Framezeit, und die Spanne enthält den Leerlauf (siehe oben). Aber der Wunsch,
die GPU-Seite zu optimieren, ist berechtigt, und dafür fehlte das Instrument: `gpu 11,56 ms`
sagt nicht, ob die Schattenkarte, die ferne Kaskade mit LOD3, der Opaque-Pass oder SheyderMods
Post-Processing die Millisekunden hält. Blind an der Schattenauflösung zu drehen wäre Raten.

Neu in `Measure/GpuFrameTimer.cs`: am Anfang jeder Render-Stage schreibt der Mess-Prefix auf
`TriggerRenderStage` eine `GL_TIMESTAMP`-Query (genau dort, wo die CPU-Stage-Uhr startet),
der End-Renderer eine letzte am Frame-Ende. Die Differenzen sind die GPU-Spanne je Stage;
eine Stage, die die Engine in diesem Frame nicht auslöst (gedrosselte ferne Kaskade, OIT
aus), hat keinen Stempel und null Zeit. Timestamps verschachteln nicht und stören die
Elapsed-Query nicht. Gelesen wird ein drei Frames alter Satz einmal die Sekunde, ein
Rückgabe-Aufruf je gestempelter Stage. `StageRing` und `Intervals` sind GL-frei und im
Harness geprüft (Slot je Frame, übersprungene Stages null, Summe gleich Spanne, Kadenz
1/s, ohne Endstempel kein offenes Intervall).

Report: `gpu je stage: before 0,6 | schatten 3,1 (fern 2,2, nah 0,9) | opaque 5,2 | oit 0,4 |
post 1,9 | ortho 0,3 | done 0,1 ms`; HUD-Zeile `gpu-stages`. Die Engine-Arbeit zwischen zwei
Triggern (Framebuffer-Wechsel, Clears) landet in der Stage davor. Was daraus folgt, entscheidet
der nächste Report: `schatten` groß gegen `opaque` heißt Schattenkarte und Kaskaden-LOD
(`ShadowMapExtraQuality`, `ShadowSkipRedundantLod`, `ShadowNearUpdateInterval`); `post` groß
heißt SheyderMods Effekte; `opaque` groß bei kleinen Schatten heißt Geometrie und
Fragment-Shader, wo eine Mod wenig zu holen hat.

### `ortho 2645,2 | gc 879,6`: der Dialog bekommt einen Namen

Der 2,7-Sekunden-Frame stand still, hatte keinen Logeintrag daneben, und die 326, 54, 67, 51
und 80 ms Ortho-Frames drumherum sehen aus wie ein Dialog, der zum ersten Mal aufgeht und dann
je Frame Tausende Kacheln zeichnet (Weltkarte bei 43.000 geladenen Chunks: 17.455
Kartenkomponenten) oder das Handbuch beim ersten Öffnen. Welcher, konnte die Zeile nicht sagen.
Jetzt: hängt ein Ruckler mit mindestens einem Viertel Ortho-Anteil an, holt der Kamera-Sampler
vor dem Commit die offenen Nicht-HUD-Dialoge aus `api.Gui.OpenedGuis`, und die Zeile endet mit
`| dialoge GuiDialogWorldMap`. Der String entsteht nur, wenn so ein Ruckler ansteht. Baseline
gleich. Die 880 ms gen1-Pause in diesem Frame ist der Preis der Allokation, die der Dialog
beim Aufbau macht; mit dem Namen ist der nächste Schritt (Vorbau beim Öffnen, Kappen der
Zeichnung je Frame) eine Entscheidung mit Adresse.

Ebenfalls aus dem Log: `readpacket11` ist UnloadServerChunk, der Tesselations-Thread hatte
`58 MB/s: rest 56` bei 217 Chunks/s (258 KB je Chunk, ungeklärt; die Stichprobe nennt Thread
und Typ), und die 10 ms für den ersten Frame eines neuen Schweins sind der Animator-Aufbau je
Shape (`pig-eurasian-adult-male` diesmal, vorher `-female`), ein Kandidat für einen Vorbau beim
Entity-Laden.

## GPU-Report gelesen: die nahe Kaskade zeichnet in 51 Millionen Texel (05.09.)

Der erste Report mit `gpu je stage` auf der RX 9070 XT: `frame 9,77 ms | gpu 8,93 ms (99 %
busy)` und darunter `schatten 7,7 (fern 1,9, nah 5,8) | opaque 0,2 | post 1,1`. Die GPU ist
die Wand (`swap 3,8` ist Warten auf sie), und 86 % ihrer Zeit gehen in die zwei Schattenpässe.
Davon die **nahe** Kaskade 5,8 ms — für ein paar Dutzend Chunks. Die ferne, die tausende
zeichnet, kostet pro gezeichnetem Frame etwa dasselbe (die 1,9 sind der Mittelwert über die
gedrosselten Frames; die Zeile sagt seit heute `= 5,7 wenn gezeichnet`).

Das ist der Fingerabdruck eines **füllratengebundenen** Tiefenpasses: die Kosten hängen an
Texeln mal Tiefenkomplexität, nicht an Geometrie. Beide Kaskaden schreiben in dieselbe
Kartengröße — `ClientPlatformWindows` rechnet `Math.Max(4, quality+2) * 1024` ein einziges Mal
und baut beide Framebuffer daraus, mit `ShadowMapExtraQuality 1` also 7168². Die ferne Karte
spannt das über 488 Blöcke (14,7 Texel je Block). Die nahe über Vanillas 39-Block-Keil, rund
60 × 34 Blöcke: **über hundert Texel je Block** auf der einen, über zweihundert auf der
anderen Achse. Niemand sieht diesen Unterschied; die GPU bezahlt ihn jeden Frame, denn 7168²
sind 51 Millionen Texel, die je Terrain-Schicht entlang des Lichtstrahls gelöscht, getestet
und geschrieben werden — mit `discard` im Fragment-Shader auch noch ohne frühes Depth-Write.

### `ShadowNearMapSize`: die nahe Karte bekommt ihre eigene Größe (Default 4096)

`ShadowResPatches.ResizeNearMap` ist ein Postfix auf `SetupDefaultFrameBuffers`: die Engine hat
die Tiefentextur der nahen Karte gerade angelegt, der Postfix spezifiziert **dasselbe
Texturobjekt** mit einem `TexImage2D` in der konfigurierten Größe neu (gleiches Format
`GL_DEPTH_COMPONENT32`, keine Daten), setzt `Width/Height` auf der `FrameBufferRef` (daraus
setzt `ClearFrameBuffer` den Viewport) und prüft einmal die Vollständigkeit des Framebuffers —
ein rückgabebehafteter GL-Aufruf pro Rebuild, nicht pro Frame; ist der Treiber nicht
einverstanden, kommt die alte Größe zurück, bevor irgendwas hineinzeichnet. Die ferne Karte
(Slot 11) bleibt unberührt: aus ihr nimmt `ShaderProgramBase` `shadowMapWidthInv` für **beide**
Kaskaden. Die Folge für die nahe: die 3×3-PCF-Taps liegen jetzt 0,57 nahe Texel auseinander
statt einem, der Kernel spannt gut zwei Texel statt drei — nahe Schattenkanten werden eine Spur
härter, etwa wie Vanilla sie bei Qualität 2 zeichnet (4096-Karte über 33 Blöcke). Kein Shader
wird angefasst.

Die Größe geht quadratisch in die Kosten: 4096 ist ein Drittel von 7168, 3072 ein Fünftel. Der
Default 4096 gibt der nahen Kaskade ~68 Texel je Block, immer noch das Vier- bis Fünffache der
fernen. **Live:** `.komet shadownear 3072` (oder `off` = Engine-Größe) setzt die Größe und baut
die Framebuffer um wie das Grafikmenü nach einer Änderung — ein Ruckler, dann die neue Zahl in
`gpu je stage`. So lässt sich 7168 gegen 4096 gegen 3072 in einer Minute vergleichen statt mit
einem Neustart pro Kandidat. Der erzwungene Rebuild beim Weltbeitritt (die Engine baut ihre
Framebuffer vor jedem Mod) kennt jetzt beide Gründe, Extra-Stufe und nahe Karte, jeden mit
eigenem „schon erledigt". Das Texel-Snapping fragt `ShadowPatches.PreparingFarCascade`, für
welche Kaskade es quantisiert — zwei Karten, zwei Raster.

HUD `nahe map 4096px 68,3 texel je block`, Report `near cascade: to 39 blocks (60 blocks
wide) | map 4096px = 68,3 texels per block`. Die Spanne der nahen Box holt ein Postfix auf
`OnRenderShadowNear`, genau wie `MatchFadeToBox` sie für die ferne holt.

### Backface-Culling in den soliden Schattenpässen (`ShadowCullBackfaces`, Default an)

`ChunkRenderer.RenderShadow` schaltet Culling für die ganze Methode aus und zeichnet vier
Render-Pässe in die Schattenkarte: Opaque (0) und TopSoil (5), dann nach einem zweiten
`GlDisableCullFace` BlendNoCull (2) und OpaqueNoCull (1). Die zweite Hälfte braucht das:
Laub, Gras, Pflanzen sind einseitige Geometrie und müssen von beiden Seiten werfen. Die erste
nicht — dieselben Pools werden im Kamera-Pass **mit** Culling gezeichnet (`RenderOpaque`
schaltet es vor Pass 0 ein und erst vor Pass 2 aus), ihre Wicklung ist also konsistent und
ihre Volumen sind geschlossen. Bei einem geschlossenen Volumen liegen die Rückseiten entlang
jedes Strahls hinter den Vorderseiten, auch entlang des Lichtstrahls: **die Tiefenkarte ist
dieselbe.** Gespart wird die Arbeit: die halben soliden Flächen in der Schattenbox, die sonst
gerastert, getestet und — je nachdem, in welcher Reihenfolge die Pools kommen — geschrieben
und wieder überschrieben werden, fallen am Primitiv weg, bevor ein Fragment existiert. Auf
einem füllratengebundenen Pass ist das die billigste Einsparung, die es gibt.

Wo die beiden Tiefenkarten abweichen können: offene Geometrie in einem soliden Pass, also eine
Fläche, deren Rückseite das Einzige entlang des Lichtstrahls ist. So ein Block ist im
Kamera-Bild von hinten schon unsichtbar — das *ist* die Definition „solider Pass"; die Kante
der geladenen Welt ist der praktische Fall, und die liegt jenseits der Ausblendung. `GL_BACK`
wird explizit gesetzt statt angenommen (die Engine setzt es beim Start und nach OIT, nichts im
Spiel setzt FRONT, ein fremder Renderer könnte).

Umgesetzt als Transpiler: der **erste** `GlDisableCullFace`-Aufruf in `RenderShadow` wird zu
`ShadowCullPatches.BeginSolidPasses(platform)` (der Empfänger liegt schon auf dem Stack), das je
nach Live-Flag Culling ein- oder ausschaltet; der zweite Aufruf bleibt und stellt No-Cull für
die Laub-Pässe her wie zuvor. Genau zwei Aufrufe müssen es sein, sonst wirft der Patch — ein
Engine-Build, der die Aufrufe verschoben hat, bekommt lieber Vanilla als den falschen Pass
gecullt. `verify` liest die IL nach dem Patch: ein `BeginSolidPasses`, ein verbliebenes
`GlDisableCullFace`, das erste vor dem zweiten; und füttert dem Transpiler eine Methode mit
nur einem Disable, die er ablehnen muss. `.komet toggle shadowcull`, Stress-Phase „shadow
backface cull off", Safemode aus, Report `solid backfaces culled`.

### `ShadowSkipRedundantLod`: pro Zelle statt pro Pool, und jetzt Default an

Die Option existiert seit 1.4x und hat im Feld nie etwas gespart — jeder Report las `lod3 in`,
weil sie den **ganzen Pool** innerhalb der LOD-Grenze verlangte, und ein Pool hält Teile von
überall her, sobald die Welt eingeströmt ist. Die Zellbox begrenzt jedes Teil in ihr; liegt
ihre fernste Ecke näher als `lod2Bias`, liegt jedes Teil-Zentrum näher, und der Kamera-Pass
zeichnet für alle davon LOD 2 statt 3 — dieselbe Exaktheit, auf einer Granularität, bei der die
Regel tatsächlich greift. Zwei `abs`, zwei Multiplikationen je Zelle im Schattenmodus; die
Überlauf-Teile außerhalb des Rasters prüfen ihre eigene Ecke. Default an, weil der Ersatz nur
dort wegfällt, wo sein detaillierter Zwilling schon in der Karte liegt: der Schatten kann sich
nur dem annähern, was die Kamera zeigt. Der neue `verify`-Test baut einen Pool aus zwei
Clustern, dessen fernste Ecke jenseits der Grenze liegt (die alte Pool-Regel spart dort
nichts), und verlangt, dass Dreiecke fallen, nur LOD 3 fällt, und nur solche, deren Zentrum die
Kamera als LOD 2 zeichnet.

### Was das bringen müsste, und was der nächste Report zeigt

Nahe Kaskade 5,8 → ~2 ms (Karte), dazu die Rückseiten in beiden Kaskaden und das doppelte Laub
— zusammen sollten aus `gpu 8,93` grob 4,5 bis 5,5 ms werden; da die CPU-Seite ohne Swap-Warten
bei rund 5 ms liegt, wird der Frame dann wieder von beiden Seiten gleich begrenzt. Ob die
Füllraten-These stimmt, entscheidet `gpu je stage` mit `.komet shadownear 7168` gegen `4096`
in derselben Szene: skaliert `nah` mit dem Quadrat der Größe, war es Füllrate; bleibt es
stehen, ist es Geometrie, und dann wäre die Tiefe der nahen Box (`ShadowBoxZExtend` 150-200
Blöcke entlang des Lichts, alle Höhlenschichten unter dem Spieler) der nächste Kandidat.

Nicht gebaut, bewusst: ein eigener Shadow-Shader ohne `discard` für die soliden Pässe (früher
Depth-Write, kein Fragment-Shader — das wäre der nächste Hebel auf derselben These, braucht
aber ein registriertes Shader-Programm samt SSBO-Variante und kollidiert mit jedem
Shader-ersetzenden Mod); die nahe Kaskade zu drosseln (`ShadowNearUpdateInterval` bleibt 1 —
der eigene Schatten würde beim Fliegen einen Frame hinterherhängen); Chunks nach
Licht-Tiefe zu sortieren (Pools sind nicht licht-kohärent, und die Range-Zusammenfassung
lebt von der Puffer-Reihenfolge).

## Zweiter Report vom 05.09.: Depth-only-Shader, Animations-Vorbau, und was „enttess" wirklich war

Der zweite Report (b260905.1133, Sichtweite 672, Flug mit 100-170 m/s durch ungenerierte
Welt) war eine andere Szene: `frame 16,37 ms | gpu 9,70 ms (69 % busy)` — CPU-gebunden, mit
`opaque 8,37` und 58 Rucklern, 47 davon mit GC-Pause. Im ruhigen HUD-Moment: 107 fps, GPU
6,24 ms, `schatten 5,7` (vorher 8,93 / 7,7 in der ersten Szene). Die `gpu je stage`-Zeile
zeigte `schatten 15,8 (fern 4,3 = 7,1 wenn gezeichnet, nah 11,6)` — eine Stage-Summe von 18 ms
gegen 9,7 ms GPU-Spanne, was nicht sein kann: die Stempel-Ringe lesen einmal die Sekunde, die
Elapsed-Query zweimal, und in einer Szene mit einem Ruckler alle 1,4 s dominieren ein paar
35-ms-Frames einen EMA mit Gewicht 0,4. Die Zeile trägt jetzt `frame by stamps X ms`, damit
man sieht, gegen welchen Frame die Stage-Zahlen zu lesen sind.

### `enttess 9,7 ms (chicken-rooster)` war die GC-Pause, nicht das Huhn

23 der 58 Ruckler buchten „before" mit `enttess 5-11 ms` und dem Namen eines Tiers, jedes Mal
ein anderer Typ: Hahn, Hirsch, Hirschkalb, Wolf. Naheliegend: der Erstframe eines neuen Typs.
Offline nachgemessen (`shapetime`, gegen die Shape-Dateien des Spiels): `InitForAnimations`
0,3-0,4 ms, Animator-Aufbau 0,04-0,35 ms, `Shape.Clone` 1,7 ms — zusammen keine 3 ms. Und der
Textur-Verdacht (`GetTextureSource`) löst sich im Code auf: der Entity-`TextureSource`
mappt nur bereits gebackene Atlas-IDs. Der Blick zurück auf die Ruckler-Zeilen: in **jeder**
steht neben `enttess` eine GC-Pause von fast derselben Größe (`gc 9,3 | enttess 9,7`,
`gc 9,7 | enttess 10,7`, `gc 4,4 | enttess 5,9`). Die Pause fror die Klammer ein, in der sie
landete — das steht so schon bei „schlechtester … davon". Die Erstframe-Kosten eines Typs
sind real, aber sie liegen woanders:

### `GenerateAllFrames`: 12 ms pro Tiertyp, auf einem Worker (`EntityAnimationPrewarm`)

`ClientAnimator.AnimNowActive` ruft `Animation.GenerateAllFrames` beim ersten Start jeder
Animation auf dem Hauptthread: Hahn 11,9 ms für 13 Animationen, „attack" allein 4,7 ms; der
Report-Wert `anim … teuerste 53,1 ms (pig)` ist dieselbe Rechnung auf einer größeren Shape.
Die Frames liegen auf den `Animation`-Objekten der **Shape**, geteilt von allen Entities des
Typs: einmal pro Typ und Sitzung, immer in dem Frame, in dem eine neue Tierart zum ersten
Mal ins Bild läuft.

Das Fenster dafür existiert seit dem Entity-Lade-Budget: jede Entity liegt vor `Initialize`
einige Frames in einem Distanz-Bin. `Runtime/AnimationWarmup.cs`: die erste gehaltene Entity
einer Shape startet einen Worker, der exakt die Cache-Miss-Sequenz der Engine fährt
(`InitForAnimations` mit denselben Argumenten — „head" plus `requireJointsForElements`,
`disableElements` —, dann `GenerateAllFrames` für jede Animation); sie und jede spätere
Entity derselben Shape bleiben gehalten, bis der Worker fertig ist (`Drain` steigt über sie
hinweg, `StatWarmupHolds`). Der Hauptthread findet danach `PrevNextKeyFrameByFrame` gesetzt
und generiert nichts; die Joint-IDs, die er in `ResolveAndFindJoints` neu ableitet, sind
dieselben (deterministisch, idempotent auf einer initialisierten Shape).

Thread-Sicherheit hängt an einer Regel: niemand sonst fasst die Shape an, solange ihr Worker
läuft. Für später ankommende Entities garantiert das der Hold; für Shapes, die schon in der
Welt sind, wird gar nicht erst gestartet — hat eine Animation schon Frames oder animiert
eine geladene Entity mit der Shape (`LoadedShapeForEntity`-Scan über `LoadedEntities`),
gilt sie als in Gebrauch, und der Lazy-Pfad der Engine bleibt. In `GenerateAllFrames` ist
der einzige geteilte Zustand die statische Identitätsmatrix, die nur geklont wird, und die
`jointsDone`-Sets pro Animation, die zur gewärmten Shape gehören;
`CacheInverseTransformMatrix` weist nur zu, wenn noch null. Promote (ein Paket nennt eine
gehaltene Entity) und der Disable-Flush warten auf einen laufenden Worker — Millisekunden,
und selten (17 von 712 im Feld). Alternate-Shapes werden wie in der Engine per
`MurmurHash3Mod` der Entity-ID gewählt, ohne die Klassen-Properties anzufassen.

`verify` lädt den echten Hahn aus den Spiel-Assets, lässt den Worker-Körper laufen und
verlangt: alle 13 Animationen haben `QuantityFrames` Frames, ein zweiter Lauf generiert
nichts, eine Shape mit Frames oder mit geladener Entity wird nie gestartet, die
Joint-Argumente sind exakt die der Engine; und der Drain steigt über eine blockierte Entity
hinweg, erledigt die freien, und holt sie nach, sobald der Worker freigibt.

### Depth-only-Shader für die soliden Schattenpässe (`ShadowDepthOnlySolidPasses`)

`chunkshadowmap.fsh` ist ein Sampler-Fetch und `if (a < 0.02) discard` — für **jedes**
Fragment **jedes** Passes, auch für die soliden Würfel, deren Texel nie transparent sind. Ein
Fragment-Shader mit `discard` kostet mehr als seine Instruktionen: die Hardware darf die
Tiefe nicht vor dem Shader schreiben, Fragmente hinter einer schon gezeichneten Fläche
werden trotzdem noch shadiert (der Test läuft früh, das Hierarchical-Z-Update wartet), und
jedes überlebende zahlt einen Textur-Fetch. Für die Pässe Opaque (0) und TopSoil (5) bindet
Komet jetzt ein Programm mit **demselben** Vertex-Shader — Quelle mit aufgelösten Includes,
Prefix-Defines (`USESSBO`, `WAVINGSTUFF` …), Include-Menge (an ihr hängt, welche Uniforms
`Use()` setzt) und Attribut-Bindungen werden aus dem **laufenden** Engine-Programm kopiert,
also auch ein von einem Shader-Mod ersetzter Vertex-Shader — und einem leeren
Fragment-Shader. Der Dateiname muss mit „chunk" beginnen: `Shader.UsesSSBOs()` entscheidet
am Namen, nicht am Code, ob die Version für den SSBO-Pfad auf 430 gehoben wird. Beide
Varianten (USESSBO 0/1) wurden offline mit `glslangValidator` gelinkt.

Der Transpiler auf `RenderShadow` ersetzt jetzt vier Aufrufe: das erste `GlDisableCullFace`
→ `BeginSolidPasses` (Culling + Programmwechsel: `Stop()` auf dem Engine-Programm, weil
`Use()` ein anderes laufendes Programm verweigert; dann `mvpMatrix` aus
`ClientMain.shadowMvpMatrix` und die Subpixel-Paddings), das zweite → `EndSolidPasses`
(No-Cull für Laub, Engine-Programm zurück), und die ersten zwei von genau vier
`Tex2d2D`-Bindings → `SetSolidTexture`, das bei aktivem Depth-only-Programm nichts tut: die
Uniform-Location des Engine-Programms auf unserem wäre ein GL-Fehler pro Pool. Zwei plus vier
müssen es sein, in dieser Reihenfolge, sonst wirft der Patch. Das Programm entsteht beim
ersten Schattenpass (Render-Thread, GL-Kontext), fällt bei jedem `ReloadShader`-Event und
wird aus dem dann aktuellen Engine-Programm neu gebaut; scheitert der Compile, bleibt das
Engine-Programm, und der Report sagt `depth-only shader NOT live (…)`. `.komet toggle
shadowdepth`, Stress-Phase „shadow depth-only shader off", Safemode aus.

### Was bleibt, und woran es hängt

Die Ruckler dieser Szene sind GC: 111 ms/s Pausen, 25 gen0/s, `promoted 89 MB/s` bei
196 MB/s Allokation, davon 92 auf dem Server (worldgen 28, chunkdb 25, tick 23) — die Kosten,
mit 100 m/s in ungenerierte Welt zu fliegen. Die Launcher-Notiz vom 30.08. hat `GCgen0size`
schon vermessen und verworfen (weniger, aber längere Pausen); der Hebel „weniger Überlebende"
ist strukturell: Chunk-Daten überleben, weil sie leben. Im Hauptthread bleibt `opaque 4,1 ms`
bei 1,2 ms Sweep unattribuiert — `.komet toggle profiler` nennt die Renderer, und der
Sonnen-Occlusion-Sync (`SunOcclusionQueryInterval`, seit 1.28.3 auf 1) ist in einer
CPU-gebundenen Szene der erste Kandidat: `.komet toggle sunquery` ist live.

## Mod-Profiler: welcher Mod kostet was — und was tut er (05.09.)

Jede Attribution in dieser Mod nennt bisher ein Stück **Engine**: eine Stage, einen Renderer,
einen Tick-Listener, einen Task-Code. Keine davon beantwortet die Frage, die jemand mit vierzig
installierten Mods tatsächlich stellt — *welcher von meinen ist es*. Die Namen in diesen
Tabellen sind Typen, und ein Typ sagt nichts über seinen Absender, solange ihn niemand
zurückbildet.

Genau das ist der ganze Mechanismus: Ein Typ nennt seine Assembly, und der Mod-Loader weiß, zu
welchem Mod jede Assembly gehört (`ModContainer.Assembly`, plus die Assemblies der ModSystems —
ein Mod darf mehrere DLLs mitbringen). Aus `Renderer-Instanz → Typ → Assembly → Mod` wird eine
Zuordnung, die kein Rätselraten ist.

**Gemessen wird nichts Neues.** Die beiden Dekoratoren, die es schon gibt (`RendererProfiler`,
`TickProfiler`), lösen beim Wickeln **einmal** ihren Mod auf und buchen ihre Ticks zusätzlich in
dessen Bucket. Der Preis der kompletten Mod-Attribution ist damit ein Feld-Add pro gemessenem
Aufruf — der Rest war ohnehin bezahlt. Die Faltung folgt der jeweiligen Kadenz: Render-Ticks
werden nur auf gemessenen Frames gefaltet (der Renderer-Profiler misst jeden vierten), Tick-Ticks
auf jedem. Andersherum stünde jeder Mod bei einem Viertel seiner Kosten.

**Was der Profiler nicht sehen kann, steht auf dem HUD** statt weggelassen zu sein:

* **Harmony-Patches.** Ein Patch läuft *innerhalb* der Methode, die er patcht — seine Zeit ist
  Teil von deren Zeit, und es gibt keinen ehrlichen Weg, sie herauszurechnen, ohne jeden fremden
  Patch selbst zu patchen. Deshalb steht die Patch-**Inventur** neben den Millisekunden: wie
  viele Methoden ein Mod patcht, wie viele davon fremder Code sind, wie viele Transpiler
  dabei sind. Ein Mod mit dreißig Transpilern in heißen Engine-Methoden ist ein Verdächtiger,
  auch wenn seine gemessene Zeit 0,00 ms ist.
* **Block-Entity-Ticks** (die tausenden Listener, die der Tick-Profiler bewusst in Ruhe lässt)
  und alles, was ein Mod auf eigenen Threads tut.
* **GUI-Dialoge**: die Engine zeichnet alle Dialoge in *einem* eigenen Renderer, ein Mod-Dialog
  landet also unter `guimanager`.
* Mit ausgeschaltetem Renderer-Profiler — dem Standard — ist überhaupt nur die Before-Stage
  gewickelt. Das HUD sagt das in **beiden** Ansichten dazu, statt einen Bruchteil der Wahrheit
  als ganze zu zeigen.

### Ladezeit pro Mod

Jeder ModSystem-Lebenszyklus-Aufruf geht durch eine einzige private Methode,
`ModLoader.TryRunModPhase(mod, system, api, phase)`. Ein Prefix/Postfix-Paar darum ist die
gesamte Messung — ein paar hundert Aufrufe pro Sitzung, zwei Stopwatch-Reads je Aufruf — und
beantwortet „warum dauert der Ladebildschirm zwei Minuten" pro Mod und pro Phase. Gepatcht wird
aus Komets eigenem `Start`, das selbst in so einem Aufruf steckt: Komet lädt bei
`ExecuteOrder 0.05`, gemessen ist also alles danach; der Report sagt das dazu, statt die Tabelle
als vollständig auszugeben. Der integrierte Server hat seinen eigenen Loader auf seinem eigenen
Thread, und im Singleplayer wartet man auf den genauso — beide Seiten werden getrennt gebucht.
Weil die Phasen laufen, *bevor* es einen Index gibt, werden sie unter Mod-Id geparkt und beim
Indexbau übernommen; der Index selbst entsteht unter demselben Lock, weil sonst der Server-Thread
in eine Dictionary schaut, die der Main-Thread gerade neu aufbaut.

Harmony bindet Patch-Parameter **über den Namen**, die Schreibweise des Loaders ist damit Teil
des Vertrags — verify pinnt sie (`mod,system,api,phase`), damit eine Umbenennung im nächsten
Spiel-Update beim Bauen auffällt und nicht als „could not enable" im Log eines Spielers.

### Das Mod-HUD

Ein **zweites** Overlay in der gegenüberliegenden Bildschirmecke, **Shift+F7** in drei Stufen
aus → kompakt → voll wie F7. Die shift-Variante der Taste, die diese Mod ohnehin besitzt, statt
einer eigenen: die frei aussehenden Tasten sind nicht frei (F6 war zuerst gebunden und ist ein
Minimap-Makro). Kollidieren können die beiden nicht — `HotKey.DidPress` vergleicht die
Modifier **exakt**, und `HotkeyManager.TriggerHotKey` läuft diesen Durchgang über alle Hotkeys
komplett durch, bevor überhaupt der Modifier-ignorierende Fallback-Durchgang startet; der
erste exakte Treffer beendet die Auslieferung. Die Zyklus-Regel ist dieselbe wie bei F7
(`DebugHud.CycleF7`, pur, von verify gepinnt) — zwei Overlays, die sich verschieden verhalten,
wären die eigentliche Überraschung. `.komet mods hud` macht dasselbe für alle, die lieber
tippen oder die Taste umbelegen. Es erbt die Maschinerie des Performance-HUDs unverändert
(`DebugHud` ist jetzt Basisklasse, `ModHud` ersetzt nur `ComposeText` und `SampleWorld`):
Cairo-Raster im Worker, adaptive Rebuild-Kadenz, die Zustandsmaschine gegen das Flackern beim
Ansichtswechsel. Was pro Overlay ist, ist Instanz-Zustand (Textur, Surface, Kadenz); statisch
blieb nur, was über die **Maschine** gilt — dass Cairo hier keinen Worker mag, was ein Rebuild
hier kostet.

Der Inhalt: `pro frame` (Anteil, ms, Balken, Quelle), `was sie tun` (Patches, fremde Patches,
Transpiler, registrierte Klassen; in der Wertespalte `Renderer/Listener`), `beim laden`
(Sekunden, teuerste Phase) und `nicht zugeordnet` — die Liste von oben. Vanilla-Inhalt
(`survival`, `game`) steht mit `*` markiert in der Tabelle: er ist oft der teuerste Posten, aber
Deinstallieren ist keine Option, die ein Spieler hat. `.komet mods` gibt dasselbe als Text aus
(englisch, wie jedes Diagnose-Artefakt hier), `.komet report` trägt es mit, `ProfileMods` und
`ModHudVisible` konfigurieren es (Config-Layout 14). Das Performance-HUD bekommt eine einzige
Zeile `mods` mit der Summe und dem Verweis auf Shift+F7 — wer die Frame-Aufteilung liest, soll
nie raten müssen, ob Mods in diesen Zahlen stecken.

## Der Deckungsrand: warum die Schatten-Drossel beim Gehen nichts gespart hat (05.09.)

Die ferne Kaskade wird seit 1.43.0 nur alle zwei bis vier Frames neu gezeichnet und dazwischen
**exakt reprojiziert** (`OffsetShadowMatrix`, von verify gepinnt). Das klingt nach drei Vierteln
Ersparnis und war keine. Der Kommentar in `ShadowThrottlePatches` sagt selbst, warum:

> Compensating the sampling matrix keeps a retained map correctly *positioned*, but it cannot
> extend what the map *covers*.

Die behaltene Tiefentextur enthält genau das Volumen, für das sie gezeichnet wurde. Verlässt die
Kamera dieses Volumen, laufen die Sample-Koordinaten aus `[0,03, 0,97]` heraus, und dort schneidet
`shadowcoords.vsh` den Schatten hart ab, statt ihn auszublenden — eine sichtbare Kante, die beim
nächsten Neuzeichnen springt. Deshalb erzwang `ShadowFarMoveThreshold = 0,15 Blöcke` ein
Neuzeichnen, sobald sich jemand bewegt: bei 85 fps sind das beim Gehen etwa drei Frames, beim
Fliegen (100 m/s ≈ 1,2 Blöcke je Frame) **jeder** Frame. Die Drossel sparte ausschließlich im
Stand — also genau dann, wenn niemand sie braucht. Der Kommentar der Stress-Phase stand seit
Monaten daneben und sagte es wörtlich: „While moving it saves nothing by design".

### Die fehlende Größe ist Deckung, und die ist berechenbar

Komets Box ist eine **Kugel um die Kamera** (`MakeBoxSymmetric`, Halbgröße
`BoxRadiusFactor × R` = `0,90/0,94 × R`). Für Kugeln ist die Frage trivial: eine Kugel mit Radius
`r + m` um C₀ enthält die Kugel mit Radius `r` um **jede** Kameraposition, die höchstens `m` von
C₀ entfernt ist. Also wird die ferne Box um `ShadowFarBoxMargin` Blöcke breiter gezeichnet, und
die Bewegungsgrenze der Drossel steigt im selben Zug auf `0,9 × m`
(`ShadowThrottlePatches.MoveLimitFor`, pur, damit verify sie festnageln kann). Die ferne Kaskade
aktualisiert dann bei `ShadowFarMaxSkip` — beim Stehen, beim Gehen und beim Fliegen gleichermaßen.

Der Rand darf **nicht** in `ShadowBox.SHADOW_DISTANCE` fließen, und das ist der eine Fallstrick:
`MatchFadeToBox` leitet die Fade-Reichweite des Shaders (`ShadowRangeFar`) genau daraus ab. Wächst
die Box über die Distanz, wächst die Ausblendung mit — und die zusätzliche Deckung wird von der
Ausblendung aufgefressen, die sie überleben sollte. Gerechnet: mit der Randbedingung
`0,94 · Halbgröße ≥ 0,90 · R + d` bleibt bei mitwachsendem R exakt `d ≤ 0` übrig. Der Rand kommt
deshalb erst in `MakeBoxSymmetric` dazu, und dann steht `d ≤ 0,94 m` da.

Zwei Dinge mussten mitwachsen, sonst ist die Kante sofort da statt in vier Frames:

* die **Cull-Reichweiten** des Sweeps. `PrepareForShadowRendering` setzt
  `shadowRangeX/Z` aus der Schattendistanz — einen Schritt *bevor* der Box-Postfix die Box
  verbreitert. Ohne Nachziehen wäre der Ring, den der Rand gerade hinzugefügt hat, leer gecullt.
  Ein Postfix (`PadCullRange`) addiert den Rand auf beide Reichweiten.
* der **Rand selbst muss mit der Box sterben**. `ShadowThrottlePatches.CoverageMargin` liest
  `ShadowPatches.EffectiveFarBoxMargin` direkt statt eine Kopie zu halten: Safemode und
  `.komet toggle shadowbox` stellen Vanillas Kegel wieder her, und eine Bewegungsgrenze, die ihre
  Box überlebt, ist wieder genau die Abrisskante. `ToVanilla`/`ToConfigured` erzwingen zusätzlich
  ein Neuzeichnen (`Invalidate`), weil die gerade behaltene Karte für die neue Boxgröße nicht mehr
  passt.

### Preis und Nachweis

Der Preis ist Texeldichte: 16 Blöcke auf einer ~255-Block-Kaskade sind +6,6 % Boxbreite, also
−6,2 % Texel je Block (14,7 → 13,8 bei 7168 px). Der zweite Posten ist der Tiefen-Bias:
`fogandlight.fsh` zieht konstant 0,0009 in normalisierter Tiefe ab, also 0,0009 × Boxtiefe in
Blöcken — die Boxtiefe wächst um dieselben 32 Blöcke, der Bias damit von ~0,56 auf ~0,59 Blöcke.
Das ist die Größe, die 1.42.1 einmal Laub-Schatten gekostet hat, damals aber um den Faktor √3
(die Würfelhülle), nicht um 5 %; unter der Dicke eines Laubblocks bleibt es mit Abstand. Dafür fällt die ferne Kaskade beim Bewegen von
„jeder Frame" auf „jeder vierte" — bei den gemessenen 5,7 ms je gezeichnetem Frame ist das die
mit Abstand größte verbliebene GPU-Position dieser Szene.

Der verify-Test prüft die Eigenschaft, nicht die Zahl: 4000 Kamerapositionen auf und in der Kugel
mit Radius `MoveLimit`, dazu je ein Punkt auf der Fade-Kugel um die **verschobene** Kamera, und
alle müssen im Band `uv ∈ [0,03, 0,97]` der bei C₀ gezeichneten Box liegen. Dazu eine
Gegenprobe: mit der ungeränderten Box muss dieselbe Bewegung das Band **verlassen**, sonst kauft
der Rand nichts. Und drittens, dass `EffectiveFarBoxMargin` und damit `MoveLimit` nach
`ToVanilla()` wieder auf dem nackten Schwellwert stehen.

HUD: `schatten-takt … · neu nach 14,4 Blöcken`. Report: `far cadence: N von M Frames gezeichnet
(X % gespart), every 2-4 frames, redraw after 14,4 blocks of camera movement (coverage margin
16)`. Live: `.komet toggle shadowmargin`, Stress-Phase „far shadow coverage margin off (redraw on
every step)" — die Phase ist die, die man **im Flug** liest, nicht im Stand.

## Die nahe Kaskade zeichnet ihre Tiefe zur Hälfte umsonst (05.09., spät)

Der GPU-Report ist eindeutig: `shadow 17,2 (far 2,3 = 8,3 when drawn, near 14,9)` von 19,7 ms
GPU-Frame. Die nahe Kaskade **ist** das Frame. Und sie hat sich nicht bewegt, als die Karte von
4096 auf 2048 px ging (21,1 → 19,9 ms) — es ist nicht die Füllrate, es ist die Geometrie, die im
Volumen steckt. Also: wie groß ist das Volumen wirklich, und wovon ist es voll?

### Was die Engine baut

```csharp
// SystemRenderShadowMap.OnRenderShadowNear
double num = 30 + 3 * (ClientSettings.ShadowMapQuality - 1);        // 39 bei Qualität 4
ShadowBox.ShadowBoxZExtend = 50f + 50f * Math.Abs(1f - sunY) + 100f; // 150..200 Blöcke
```

```csharp
// ShadowBox.update(), am Ende
minZ += 0.0;
maxZ += ShadowBoxZExtend;
```

Das ist **richtig**, und zwar exakt richtig. Der Lichtraum schaut die Sonne an —
`Mat4d.LookAt(lightViewMatrix, sunPosition, (0,0,0), (0,1,0))`, also zeigt +z **zur Sonne** — und
nur Geometrie mit *höherem* Licht-z als ein Empfänger kann ihn beschatten. Deshalb wird die Box
nach oben verlängert und nur nach oben: `maxZ` steigt, `minZ` bleibt.

### Und was die Projektion daraus macht

```csharp
private void loadOrthoModeMatrix(double[] projectionMatrix, double width, double height, double length)
{
    Mat4d.Identity(projectionMatrix);
    projectionMatrix[0]  =  2.0 / width;
    projectionMatrix[5]  =  2.0 / height;
    projectionMatrix[10] = -2.0 / length;
    projectionMatrix[15] =  1.0;
}
```

Keine Translation. Eine Ortho-Matrix ohne Translation clippt `|z| <= length/2` **um den
Licht­raum-Ursprung** — sie benutzt nur die *Länge* der Box und erfährt nie, **wo** die Box liegt.
Die ganze Sorgfalt in `update()` ist damit weg: Das gezeichnete Volumen ist
`[−length/2, +length/2]`, die Verlängerung landet zur Hälfte oben und **zur Hälfte unten**.

Diese untere Hälfte sind rund neunzig Blöcke Welt *hinter* jedem Empfänger, den sie treffen
könnte — aus Sicht der Sonne dahinter. Sie wird jedes Frame in die Nahkarte gezeichnet und kann
kein einziges Fragment verdunkeln.

### Die Untergrenze steht im Shader

Nach unten muss das Volumen nur so weit reichen, wie es noch **Empfänger** gibt, und das legt
`shadowcoords.vsh` auf die Nachkommastelle fest:

```glsl
float distanceNear = clamp(
    ... + max(0.0, len / shadowRangeNear - 0.15)
, 0.0, 1.0);
nearSub = shadowCoordsNear.w = clamp(1.0 - distanceNear, 0.0, 1.0);
```

Das Gewicht der Nahkarte ist null jenseits von `1,15 × shadowRangeNear` = 45 Blöcken. Weiter weg
liest kein Pixel die Nahkarte mehr, egal was drinsteht. Der Lichtraum ist eine Rotation, also ist
diese euklidische Schranke zugleich eine Schranke auf das Licht-z. Dazu ein Rand für den Knick
`max(0.0, shadowCoordsNear.z - 0.98) * 100` — der schneidet die Nahkarte hart ab, nicht weich, ein
Empfänger darf da nicht hineinlaufen.

### Der Eingriff: ein Term

`ShadowDepthPatches.FitDepthRange`, ein zweiter Postfix auf `loadOrthoModeMatrix` (der erste ist
das Texel-Snapping, der schreibt `[12]`/`[13]`, dieser `[10]`/`[14]`):

- **Obere Ebene bleibt exakt vanillas** `+length/2`. Damit wird kein Verdecker weggelassen, den
  vanilla gezeichnet hat — das Bild ändert sich nicht, und das ist der Grund, warum das hier
  Default sein darf.
- **Untere Ebene** auf den tiefsten Empfänger, den die Nahkarte noch bedient, plus Knick-Rand.
- Nie länger als vanilla; wo die neue Ebene tiefer läge als vanillas, gilt vanillas.

Alles Nachgelagerte hängt an derselben Matrix — die gepushte `PMatrix`, `shadowMvpMatrix`,
`toShadowMapSpaceMatrixNear` und die sechs Ebenen, die `CalcFrustumEquations` aus `PMatrix.Top`
zieht. Lookup und CPU-Cull folgen von allein.

Mit den Feldzahlen (39-Block-Kaskade, 182 Blöcke Extend, 237 Blöcke Volumen): **27 % der Tiefe
weg, bei identischem Bild.**

### Gratis dazu

`fogandlight.fsh` zieht für die Nahkarte konstant `0,0005` in **normalisierter** Tiefe ab, also
`0,0005 × Boxtiefe` in Blöcken. 237 → 172 Blöcke heißt Bias 0,119 → 0,086 Blöcke. Genau diese
Größe ist das, woraus Peter-Panning unter Laub gemacht ist. Und der Tiefenpuffer verteilt dieselbe
Präzision über weniger Welt.

### Ferne Kaskade: absichtlich nicht

Derselbe Defekt, aber: 2–6 ms amortisiert gegen 15–18, die Karte wird über Frames behalten und
reprojiziert (`ShadowThrottlePatches.OffsetShadowMatrix`), und ihre Box wird ohnehin komplett
ersetzt (`MakeBoxSymmetric`). Eine Kaskade nach der anderen, jede mit einer Zahl dahinter.

### Nachweis

verify prüft die Eigenschaft über 3 Reichweiten × 4 Boxlängen × 4 Kamerapositionen: die obere
Ebene bewegt sich **nie**, das Volumen wächst **nie**, jeder Empfänger innerhalb `1,15 × range`
liegt drin und unter Tiefe 0,98, und die beiden Matrixterme bilden den Bereich exakt auf
`[−1, +1]` ab. Dazu die Gegenprobe, dass der Zuschnitt auf den Feldzahlen mindestens 20 % bringt —
ein Patch, der nichts spart, soll auffallen.

`ShadowNearDepthFit` in komet.json, `.komet toggle shadownearfit` live, Report:
`near depth: 182 blocks (the engine's), volume 172 of 237 blocks deep (27 % of it cut down-sun)`.

### Und `.komet shadowneardepth` bedeutet jetzt, was es sagt

Die Kappung des Extends hat vorher das Volumen *symmetrisch* gekürzt — von jedem Block, den sie
weggenommen hat, kam die Hälfte aus der Hälfte, die ohnehin nichts konnte. Mit dem Fit ist die
Aufgabenteilung sauber: der Fit nimmt den nutzlosen Teil zum Nulltarif, die Kappung handelt
darüber hinaus Reichweite gegen Tempo — und nur letzteres ist eine Ermessensfrage.

### Korrektur: 6 %, nicht 27 % — der Lichtraum-Ursprung liegt 50 Blöcke sonnenwärts

Der nächste Report sagte `volume 221 of 236 blocks deep (6 % of it cut down-sun)`. Der Fehler
in der Rechnung oben: Ich hatte die Kamera bei Licht-z ≈ −1 angenommen („die Einheits-Eye der
LookAt"). `ClientGameCalendar.Update` setzt aber

```csharp
SunPosition.Set(SunPositionNormalized).Mul(50f);
```

und beide LookAts (`lightViewMatrix` beim Zeichnen, `array` für die Cull-Ebenen) setzen ihr Auge
genau dorthin. Der Lichtraum-Ursprung, um den die Ortho zentriert, ist also **50 Blöcke
sonnenwärts der Kamera**. Relativ zur Kamera ist vanillas Volumen damit
`[−236/2 + 50, +236/2 + 50] = [−68, +168]`. Die Empfänger brauchen 45 der 68 nach unten; der Fit
nimmt den Rest — 15 Blöcke, 6 %. Der Fit selbst hat korrekt gerechnet (er liest `lightView[14]`
und nimmt nichts an); falsch war nur, was ich daraus versprochen habe. verify rechnet die
Feldzahlen jetzt mit der Kamera bei −50 nach und verlangt 4–10 %, nicht 20.

**Wichtiger als die Zahl:** Für flaches Gelände sitzt der Preis der Nahkaskade gar nicht in der
Tiefe. Das Volumen ist ein geneigter Balken 63 × H × 236 entlang der Sonne; er trifft die
(horizontale) Geländeoberfläche in einem Streifen der Länge `H / sin(Elevation)`. Was gezeichnet
wird, ist dieser Streifen — samt Bäumen darauf — und der wird von `ShadowBoxZExtend` **nicht**
kürzer, solange der Balken oben und unten aus dem Gelände herausragt (68 nach unten reicht
immer, 168 nach oben fast immer). Die Tiefe zählt nur, wo Gelände sonnenwärts in den Balken
hineinragt: der Berg gegen die Sonne, die Klippe, unter der man steht.

Das macht `.komet shadowneardepth` zu einem schwachen Hebel — und es war zusätzlich ein
gefährlicher: Bei 80 Blöcken stand die untere Ebene 23 Blöcke unter der Kamera, die Nahkarte
bedient aber Empfänger bis 45. Auf flachem Boden verloren Empfänger ihren Nah-Schatten, genau
dort, wo die Kappung „nichts kosten" sollte. Mit dem Fit ist die untere Ebene aus den Empfängern
abgeleitet und bleibt, wo sie ist, egal was die Kappung mit dem oberen Ende macht — verify
prüft den 80er-Fall.

### Und ein Fehler im engen Cull-Band von gestern

Dieselbe Fünfzig steckt noch woanders. `TightenCullRange` hat den Bereichstest der Nahkaskade
auf `halfX + 48` verengt, mit dem Kommentar „plus der Einheits-Eye-Versatz der Lichtmatrix".
Der Versatz ist nicht eins, er ist `50 · |sun.x|` — bei 35° Sonne 41 Blöcke, und vom Pad bleiben
nach dem Teilradius (27,7) nur 20. Ein Band von Verdeckern an der sonnenwärtigen Kante des
Volumens wurde vom Bereichstest verworfen, das die Ebenen behalten hätten: die langen Schatten
eines Hügels gegen die Sonne, in der Nahkarte weg (in der Fernkarte noch, also halbe Stärke).
Jetzt ein eigener Term (`ShadowPatches.EyeOffsets`), und verify setzt das Auge dahin, wo das
Spiel es setzt — ohne den Term fällt der Test durch.

## Die Nahkaskade zeichnet nur noch, was auf etwas Sichtbares fallen kann (05.09., Nacht)

Der GPU-Hebel, der nach alledem übrig bleibt, ist der Streifen selbst: nicht *wie tief*, sondern
*wie breit* die Nahkaskade zeichnet. Ihre Box folgt dem Blick nicht (`getCameraRotationMatrix`
ist die Identität), ihre Karte bedient Empfänger in jede Richtung — und der Boden hinter der
Kamera ist nie auf dem Schirm. Jeder Verdecker, dessen Schatten nur dort landet, wird umsonst
gezeichnet; im Wald ist das der Großteil der Bäume hinter und neben einem.

Was einen sichtbaren Empfänger erreichen *kann*, ist exakt:

- die Nahkarte bedient Empfänger nur bis `1,15 × range` = 45 Blöcke (`shadowcoords.vsh`);
- gezeichnet werden nur Empfänger im Kamerafrustum;
- ein Verdecker beschattet entlang der Lichtrichtung, im Lichtraum die z-Achse.

Ein Verdecker zählt also nur, wenn sein Licht-(x, y) auch ein Empfänger im Frustum-Ausschnitt
bis 45 Blöcke hat. Und die vier seitlichen Clip-Ebenen der Schattenprojektion sind Ebenen
konstanten Licht-x bzw. Licht-y. `ShadowFootprintPatches` zieht genau diese vier Ebenen an den
Ausschnitt heran — an den Ebenen, die die Engine selbst in `PrepareForShadowRendering` gebaut
hat, erkannt daran, dass ihre Normale senkrecht zum Licht steht (nicht am Index). Pro Ebene:
das Minimum der vorzeichenbehafteten Distanz über die fünf Ecken des Ausschnitts (konvex, linear
→ das Minimum liegt in einer Ecke), zusätzlich beschränkt durch Kugelmitte minus Radius; das
größere der beiden ist immer noch eine untere Schranke. Davon 8° Winkel plus zweimal die Drehung
des letzten Frames plus 4 Blöcke Pad ab, den Rest rückt die Ebene hinein.

Vanillas `InFrustumShadowPass` und der `FastCuller` lesen dieselben Ebenenfelder — beide Pfade
folgen, der Cull-Verifier bleibt gültig. Abdeckung, Texelraster und Lookup der Karte ändern sich
nicht; die Karte enthält „kein Verdecker", wo nichts Sichtbares einen hätte lesen können.

Was das bringt, hängt vom Blick relativ zur Sonne ab: quer zum Licht etwa die Hälfte der
Geometrie, in oder gegen die Sonne weniger, beim Blick auf den Boden das meiste. Die Zahl steht
im Report: `near pass: N triangles … | footprint X % of the box`, mit den Dreiecken des
Kamera-Passes daneben — damit die GPU-Millisekunden der Nahkaskade endlich als ms/Dreieck lesbar
sind.

Es tritt zurück, wenn die Nahkaskade über Frames behalten wird (`.komet shadownearskip`): eine
behaltene Karte, auf einen Blick zugeschnitten, wäre für den nächsten falsch. Die Fernkaskade
bleibt aus demselben Grund unangetastet — ihre Karte *wird* behalten und reprojiziert, und eine
Drehung ist für sie genau deshalb gratis, weil sie jede Richtung abdeckt.

verify: 3 Elevationen × 3 Azimute × 4 Yaws × 3 Pitches, je 1500 zufällige Sonnenstrahlen durch
sichtbare Empfänger — jeder Verdecker, den vanilla auf so einem Strahl behalten hat, bleibt;
die Tiefenebenen bewegen sich nie; mindestens eine Blickrichtung spart etwas.


## Der Report, der die Sonde erzwingt: 593k Dreiecke für 17 ms (05.09., spät nachts)

Mit den Dreiecken je Pass im Report steht die Nahkaskade nackt da:

```
gpu per stage: … shadow 17,5 (far 0,2, near 17,4) | opaque 0,0 …
near pass: 593.438 triangles in 98 ranges per frame (camera pass 17.266.653 triangles)
```

593 Tausend Dreiecke gegen 17 Millionen im Kamera-Pass — und die 593k sollen 17 ms kosten, die
17 Mio. **0,0**. Das ist keine Geometrie, das kann keine sein. Und es ist auch keine
Füllrate-Aussage, denn die Zeile darüber ist nicht das, was sie zu sein scheint.

### Timestamps sind top-of-pipe

`glQueryCounter(GL_TIMESTAMP)` schreibt die Zeit, wenn der Command-Processor den Befehl
**erreicht** — nicht, wenn die Arbeit davor **fertig** ist. Draw-Calls werden in Mikrosekunden
abgesetzt und rechnen später; die nächste Barriere (ein Framebuffer-Clear, eine Textur, in die
gerade gezeichnet wurde und die jetzt gesampelt wird) hält den CP an, bis alles davor fertig
ist — und die Spanne, in der diese Barriere liegt, erbt alles, was noch in der Pipeline war.
Der Clear der Nahkarte ist so eine Barriere. Also landen im „near"-Span: der Nah-Pass selbst
**plus der Rest des vorigen Frames** — Opaque, OIT, Post. Die 17 Mio. Dreiecke des Kamera-Passes
kosten nicht 0,0 ms; sie werden in 0,0 ms *abgesetzt* und rechnen in der Schattenspanne des
nächsten Frames zu Ende.

Drei Optimierungen an der Nahkaskade (Tiefen-Fit, Kappung, Sichtbarkeits-Zuschnitt) wurden auf
diese Zeile hin gebaut, bevor die Dreiecke daneben standen. Der Fit und der Zuschnitt sind
korrekt und bleiben — sie sparen, was sie sparen, nur eben von einem kleineren Betrag als
gedacht. Was fehlt, ist das Instrument, das nicht lügt.

### `GL_TIME_ELAPSED` ist bottom-of-pipe

Eine Elapsed-Query endet, wenn die eingeschlossenen Befehle **abgeschlossen** sind. Eine Klammer
um einen Pass ist die Zeit, die dieser Pass wirklich gebraucht hat — egal, was davor noch in der
Pipeline hing. Und `GL_FRAGMENT_SHADER_INVOCATIONS` (ARB_pipeline_statistics_query) zählt, was
der Pass **schattiert** hat: die eine Zahl, die „Füllrate oder Geometrie" ohne Argument
entscheidet.

`GpuPassProbe` klammert die solide und die Laub-Hälfte beider Kaskaden (die transpilierte Grenze
gehört ohnehin `ShadowCullPatches`) und `ChunkRenderer.RenderOpaque`, jedes dritte Frame. Nur
eine Elapsed-Query darf aktiv sein, also setzt der Frame-Timer in diesen Frames seine eigene aus
— zwei von drei Samples bleiben ihm. Gelesen wird vier Sonden später und nur, wenn der Treiber
das Ergebnis als fertig meldet; ein Lesen wartet nie.

```
gpu per pass (elapsed, every 3. frame): near solid X ms / N Mfrag, near foliage Y ms / M Mfrag | camera opaque Z ms / K Mfrag
```

### Die Hypothese, die dran ist

65,6 Texel je Block in der Nahkarte, ein Laubdach von der Sonne aus gesehen zehn bis dreißig
Lagen tief, jede Lage Textur-Fetch plus `discard` (kein früher Tiefen-Write). Das wären
Hunderte Millionen Fragmente pro Frame — Füllrate, keine Geometrie. Wenn die Sonde das sagt, ist
der Umbau klar: die Laub-Pässe in eine **kleinere** Tiefenkarte zeichnen (2048² entspricht exakt
der 32-px-Textur, verliert also kein Alpha-Loch) und per Vollbild-`min` in die 4096²-Karte
mischen — die solide Geometrie behält ihre Schärfe, das Laub kostet ein Viertel. Wenn die Sonde
etwas anderes sagt, wird das nicht gebaut. Bis dahin: `.komet toggle shadowfoliage` lässt die
Laub-Pässe in beiden Schattenkarten weg — ein Blick auf die Frame-Zeit, und die Frage ist
beantwortet.

### Und die Partikel

`particles: 660 alive on the main pools (16,14 ms/frame)`. `TickFixedStep` macht nur alle
`PhysicsTickTime` (1/16 s) einen Schritt — 660 Partikel können keine 16 ms Physik sein. Aber
`glBufferSubData` auf einen Instanzpuffer, den die GPU noch liest, blockiert den Render-Thread,
bis die GPU aufgeholt hat: das Warten eines GPU-limitierten Frames taucht dort auf, wo die CPU
als nächstes einen belegten Puffer anfasst. `Platform.UpdateMesh` wird innerhalb von
`OnNewFrame` geklammert; die Zeile liest jetzt `physics X + upload Y`.


## Die Sonde antwortet: die Nahkaskade kostet 1,3 ms, der Kamera-Pass ist der Berg (06.09.)

```
gpu per pass (elapsed, every 3. frame, 5.106 samples): near solid 0,1 ms / 0 frag, near foliage 1,2 ms / 100 Mfrag | camera opaque 5,2 ms / 23 Mfrag | far when drawn: solid 1,3 ms / 0 frag, foliage 3,4 ms / 131 Mfrag
near pass: 234.537 triangles (camera pass 6.068.585 triangles)
particles: 117 alive (0,07 ms/frame: physics 0,00 + upload 0,07)
```

Bottom-of-pipe gemessen ist die Nahkaskade **1,3 ms** — nicht 17. Die Stage-Zeile daneben sagt
weiter `near 8,2`, und das ist jetzt ohne Argument als Erbschaft der vorigen Frame-Hälfte
erklärt. Der teuerste Pass ist der **Kamera-Opaque-Pass: 5,2 ms für 6 Mio. Dreiecke, 23 Mio.
Fragmente auf 3,7 Mio. Pixel** — sechs Fragmente je Pixel, was bei Dreiecken unter Pixelgröße
das Quad-Overdraw ist (jedes Sub-Pixel-Dreieck spawnt ein 2×2-Quad). Hochgerechnet auf die
17 Mio. Dreiecke des Wald-Reports sind das die ~15 ms, die dort als „near" gebucht waren.

Der Nutzer hatte von Anfang an recht: „Blätter/Unkraut". Blätter sind `renderpass:
OpaqueNoCull` mit `faceCullMode: CollapseMaterial` und einem `lod0Shape` bis 211 Blöcke — aber
**ohne LOD-2-Ersatz** (`doNotRenderAtLod2` tragen nur die Aquatik-Blöcke). Ein Wald wird also
Blatt für Blatt bis zur Sichtweite gezeichnet.

### Das Instrument dazu: Dreiecke je Pass × Entfernung × LOD

Der Sweep bucht jedes emittierte Teil in eine (Pass, Band, LOD)-Tabelle — eine Addition je
Teil, thread-lokal, am Frame-Rand gefaltet. Den Pass liefert ein Prefix auf
`MeshDataPoolManager.Render` (`PoolPassPatches`), der den Manager per Referenz in
`ChunkRenderer.poolsByRenderPass` nachschlägt; ein Pool wechselt nie den Manager, also merkt
sich der Pool-Cache den Pass. Drei Zeilen im Report: je Pass, je Entfernungsband mit
Laub-Anteil, je LOD.

### Der grobe Hebel, ehrlich bepreist: `.komet foliagerange`

Jenseits der Reichweite werden OpaqueNoCull und BlendNoCull im Kamera-Pass nicht gezeichnet —
ein Baum dort ist ein Stamm. Technisch eine Kappung der LOD-Distanztabelle für Laub-Pools (der
Sweep kostet nichts extra), der Cull-Verifier schaut bei diesen Sweeps weg. Default vanilla;
Safemode schaltet es ab. Der richtige Hebel — ein LOD-2-Ersatz für Blätter oder weniger Flächen
je Blattblock — kommt, sobald die Histogramm-Zeilen sagen, ob die Dreiecke im Nahbereich oder in
der Ferne sitzen.

### Partikel: `physics 0,00 + upload 0,07`

Bei 117 Partikeln und einem nicht GPU-limitierten Frame ist der Upload 0,07 ms. Die 16 ms im
Wald-Report stehen damit noch als „Warten auf die GPU im Upload" im Raum, unbewiesen — die Zeile
entscheidet es beim nächsten GPU-limitierten Frame.


## Pools als Orte: der Kamera-Pass von vorn nach hinten (06.09.)

Die Sonde hat den Kamera-Pass zerlegt: **39 Mio. geshadete Fragmente auf 3,7 Mio. Pixel** —
zehn je Pixel, eines zählt — und der Fragment-Shader ist etwa die Hälfte des Passes (4,3 ms mit
vollem, 3,4 ms mit flachem Shader bei 50 % mehr Dreiecken). Der Tiefentest ist ordnungsunabhängig
in dem, was er *behält*, nicht in dem, was er *kostet*: ein Fragment hinter einer schon
geschriebenen näheren Tiefe wird verworfen, bevor der Shader läuft; eines, das vor seinem
Verdecker gezeichnet wird, wird geshadet und dann überschrieben. Von vorn nach hinten zeichnen
macht aus den meisten der neun anderen Fragmente Verwerfungen.

Das war bisher unmöglich, und der Grund sitzt in `MeshDataPoolManager.AddModel`: „erster Pool
mit Platz". Chunks kommen in Server- und Tesselationsreihenfolge, also enthält jeder Pool Teile
von überall, und jeder Pool-Draw deckt die ganze Sicht ab. Sortiert man die Teile *innerhalb*
eines Pools, ordnet man 1/513 der Welt; zwischen Pools bleibt die Reihenfolge Zufall — und genau
dort entsteht der Overdraw.

### `SpatialPools`: Routing nach Region

Ein Prefix auf `AddModel` ersetzt die Kandidatenliste: statt aller Pools die des Regions-
Schlüssels `(x >> 7, z >> 7)` — 128 × 128 Blöcke, alle Höhen (arithmetischer Shift, damit
negative Koordinaten wie positive flooren; verify prüft −1 und −128). Sind die Pools der Region
voll, entsteht ein neuer *für die Region* — dieselbe Größenregel, dieselbe Registrierung beim
Master-Pool, dieselbe Ursprungsregel wie im Original. Vom Reclaimer geleerte Pools (Kapazität 0)
verweigern `TryAdd` ohnehin und fallen beim nächsten Fehlversuch aus der Regionsliste.
Mini-Dimensionen (dimension 1) gehen unverändert den Vanilla-Weg; ihre Pools werden per Ursprung
nachgeschlagen, da darf nichts dazwischen.

Ein Pool ist damit ein Ort. Seine gecachte Box (die der `FastCuller` ohnehin hält) ist klein,
und **`PoolPassPatches.NotePass` sortiert die Pool-Liste des Managers einmal je Frame nach
Entfernung**, bevor die Engine-Schleife sie liest — nur im Kamera-Pass; die Schattenpässe sind
Depth-only und behalten ihre Reihenfolge. Die Liste bleibt dasselbe Objekt, nur die Ordnung
ändert sich; nichts in der Engine hält Indizes hinein.

### Innerhalb des Pools: Zellen nach Entfernung

Der Sweep emittierte bisher in Index-Reihenfolge (Bitmap-Scan), damit benachbarte Ranges
verschmelzen. Im sortierten Modus läuft er die Zellen des Gitters nach Entfernung ihres
Mittelpunkts ab, je Zelle die Buckets in Bucket-Reihenfolge — innerhalb eines Buckets sind die
Indizes aufsteigend, also überleben die Rücken-an-Rücken-Merges innerhalb einer Zelle. Danach die
seit dem letzten Rebuild angehängten Teile, die noch keine Zelle haben. Gap-Bridging ist im
sortierten Sweep aus: es läuft die Teileliste zwischen zwei emittierten Teilen in Indexreihenfolge
ab, und die gibt es nicht mehr.

Der Cull-Verifier vergleicht Ranges gegen Vanillas Liste *in Emissionsreihenfolge* — er ist
ordnungsabhängig (verify beweist das mit drei Teilen in falscher Reihenfolge). `Maybe()` sortiert
die emittierten Ranges deshalb vor dem Vergleich nach Byte-Start zurück: geprüft wird die
*Menge*, und die ist unverändert.

### Preis und Zahl

Regions-Pools füllen sich ungleichmäßiger als ein globales First-Fit, es gibt also mehr,
teils halbleere. Der Reclaimer gibt leere zurück; der Report zeigt `draw order: nearest first
(… pool sorts) | pools: routed by 128-block region, N regions holding M pools, … models routed,
… pools created, … handed to vanilla`. Und `draw ranges` daneben sagt, was das Bridging gekostet
hat. `.komet toggle spatialpools` gilt für alle ab dann eingefügten Modelle, `fronttoback` sofort.

### Ergebnis: abgeschaltet, mit Zahl

Der Report nach dem Neubetreten: **1.917 Pools à 56 Teile** (First-Fit: 513 à 289), jeder in
voller Engine-Größe alloziert — das Vierfache an Videospeicher, Allokationsstände von 0,3 bis
**7,9 Sekunden**, während der Treiber auslagerte, 21 fps. Regionen von 128 Blöcken sind für einen
Pass zu leer: viele Chunks tragen in einem gegebenen Pass nichts bei, und ein Pool je Region und
Manager bleibt bei einem Fünftel der Füllung.

Und das Eigentliche trat nicht ein: **40 Mio. geshadete Fragmente von vorn nach hinten gegen
39 Mio. in Indexreihenfolge.** Der Tiefentest verwirft unter dem Chunk-Shader nicht früh — ein
Shader mit `discard` schreibt Tiefe spät, und der frühe Test hat nichts Endgültiges, wogegen er
verwerfen könnte. Die Zeichenreihenfolge erreicht den Fragment-Shader gar nicht. Ein
Tiefen-Vorpass gäbe ihm etwas Endgültiges — und kostet ein zweites Front-End (~2,3 ms) plus
triviale Fragmente, um ~1,8 ms Shading zu sparen: netto Verlust in dieser Szene.

Beides bleibt im Code, per Default aus (`SpatialPools`, `FrontToBack` = false, Layout 21). Was
übrig ist, hatte das Histogramm vor dem Experiment schon gesagt: weniger Dreiecke jenseits von
640 Blöcken, sonst nichts.

## Verschmolzene Fernflächen: jenseits von 640 Blöcken Rechtecke statt Blöcke (06.09.)

Das Histogramm hatte es gesagt, das Pool-Experiment hat es bestätigt: der Kamera-Pass wird nur
durch weniger Dreiecke jenseits von 640 Blöcken billiger. Dort deckt ein Block 1,6 Pixel, und
die Messung mit flachem Fragment-Shader hatte die Hälfte des Passes im Front-End verortet —
Dreiecke rastern, die kleiner als ein Pixel sind und trotzdem jedes ein volles 2×2-Fragment-Quad
anstoßen. Nichts am Bild braucht diese Flächen einzeln: zwanzig Grasoberseiten in 640 Blöcken
sind eine 32 Pixel lange Linie, deren Textur die Mip-Kette ohnehin auf eine Farbe je Block
mittelt.

### Was verschmolzen wird — und was nicht

Auf dem Tesselations-Thread, nach `ChunkTesselator.NowProcessChunk`, wird das LOD-1-Mesh jedes
Opaque- und TopSoil-Teils zerlegt. Verschmolzen werden nur achsenparallele Einheitsflächen:
vier Vertices in einer Ebene, genau ein Block in jeder Ebenenrichtung, an ganzzahligen
Positionen; identische Vertex-Flags (Normale, Glow, Z-Offset), kein Wind, keine Spiegelung;
dieselbe Kachel im Atlas; übereinstimmende Colormap-Daten (Temperatur und Regen in Vierer-
Stufen); und Vertex-Licht, das über das Rechteck entweder gleich ist oder linear verläuft
(Toleranz 3/255 je Kanal, geteilte Kanten 2/255). Alles andere — Treppen, Platten, Zäune,
Meißelblöcke, Laubkreuze, ein Fackel-Gradient — bleibt, wie die Engine es tesseliert hat, und
wird in jeder Entfernung so gezeichnet. Rechtecke sind auf 16×16 Blöcke begrenzt: die
Chunk-Shader werten das Rauschen der Saisontönung je Vertex aus und interpolieren über die
Fläche; ein chunkbreites Rechteck glättete diese Fleckigkeit über 32 Blöcke.

Heraus kommen drei Meshes je Teil: das Engine-Mesh, um die verschmelzbaren Flächen erleichtert
(an Ort und Stelle kompaktiert, Reihenfolge erhalten); die verschmelzbaren Flächen unverschmolzen
(`near`); die Rechtecke (`far`). Die Rechtecke behalten die Vertex-Reihenfolge ihrer
Ursprungsfläche — Winding, Backface-Culling und die SSBO-Face-Packung (v1 = v0 + 2a, v3 = v0 +
2b, v2 = v0 + 2a + 2b) tragen sich so durch. Das Licht an den vier Ecken kommt von den
Eckflächen.

### Zwei LOD-Stufen, die die Engine nie vergibt

Beide Extra-Meshes gehen nach `TesselatedChunkPart.AddToPools` in denselben Pool-Manager: die
Zwillinge mit LOD-Stufe 5, die Rechtecke mit 4, und beide in die Location-Liste des Chunks, so
dass die Engine sie mit dem Chunk entfernt wie ihre eigenen. Der Sweep zeichnet 5 diesseits der
Entfernung und 4 jenseits (`BuildLodBounds`, Einträge 4 und 5; die Buckets je Zelle wurden dafür
von vier auf acht erweitert). `InFrustumAndRange` der Engine liefert für beide Stufen `false` —
das ist das Sicherheitsnetz: ist der Sweep nicht unser oder das Feature aus, setzt `SyncMode`
die Zwillinge auf Stufe 1 (überall gezeichnet, wie die Flächen zuvor) und versteckt die
Rechtecke. Das Bild ist dann die Engine, ohne eine Retesselation. In den Schattenpässen werfen
die Rechtecke und nicht die Zwillinge: dieselben Oberflächen, ein Bruchteil der Dreiecke. Der
Cull-Verifier kennt die Regel in seiner Vanilla-Referenz, und die Zufallsläufe der Äquivalenz
tragen die beiden Stufen, gezeichnet und nicht gezeichnet.

### Der eine Shader-Eingriff

Ein Rechteck trägt auf allen vier Vertices die Kachelmitte als Texturkoordinate. So abgetastet,
wie die Engine abtastet, zeigt das den Mitteltexel der Kachel über zwanzig Blöcke — einen
Sprenkel einer rauschigen Textur, gedehnt. Was das Rechteck zeigen muss, ist, was die zwanzig
Flächen in dieser Entfernung zeigten: die von der Mip-Kette gemittelte Kachel. Also bekommen
`chunkopaque.fsh` und `chunktopsoil.fsh` einen Umbau am geladenen Quelltext (eine Shader-Mod
wird geerbt; fehlt der Fetch, schaltet sich das Feature ab): jeder Terrain-Fetch läuft durch
eine Funktion, die bei jeder Ableitung der Koordinate den normalen Fetch zurückgibt — jede
tesselierte Fläche spannt ihre Kachel, also hat jede eine — und sonst vier Taps auf der
gröbsten Mip-Stufe, eine Viertelkachel neben der Mitte. Mit den drei Mip-Stufen der
Voreinstellung ist die gröbste 4×4 Texel je 32-Pixel-Kachel, jeder bilineare Tap mittelt einen
2×2-Block, die vier zusammen sind der exakte Kachelmittelwert. Für alles, was die Engine
tesseliert hat, ist die Variante der alte Fetch plus `fwidth` und ein Vergleich.

Die Uniforms (`kometFarLod` = Mip-Stufe, `kometTileQuarter` = Kachelgröße/4) setzt der
Render-Prefix des Pool-Managers, wenn das gebundene Programm sie kennt. Ein Shader-Reload der
Engine ersetzt die Programmobjekte; `FarMeshShaders.Ensure` bemerkt die neue Instanz vor dem
nächsten Opaque-Pass und legt die Variante erneut an. Beim Verlassen der Welt kommen die
Engine-Quellen zurück.

### Preis und Erwartung

Der Verschmelzer alloziert außer den zwei Ausgabe-Meshes (aus dem Recycler) nichts; der Report
bepreist ihn je Teil. Die Rechtecke kosten ihre Vertices zusätzlich zu denen der Engine. Die
Erwartung aus den Zahlen: 57 % der fernen Dreiecke sind Gelände und Oberboden; wo davon ein
Großteil zu Rechtecken wird, fällt das Front-End des Kamera-Passes um den Anteil und mit ihm
die Fragment-Quads, die bisher je Subpixel-Dreieck neu anfingen. Laubkreuze verschmelzen nicht;
dafür bleibt `.komet foliagerange`. Was das im Wald ausmacht, sagt die Zeile `far mesh` und die
GPU-Zeile des nächsten Reports — nicht dieser Text.

## `.komet alloctrace`: das Spiel zeichnet seine Allokationen mit Aufrufstapeln auf (06.09.)

Der Sampler im Prozess konnte Thread und Typ nennen — „Int32[] auf dem Tesselations-Thread" —
und nicht mehr: die Laufzeit liefert ihre Allocation-Ticks an einen Listener ohne den Stapel.
EventPipe führt zu jedem Tick den Stapel mit, und der Diagnose-Port eines .NET-Prozesses nimmt
eine Sitzung vom Prozess selbst an. Also zeichnet das Spiel sich N Sekunden lang selbst auf
(GC-Keyword, verbose) in eine `.nettrace` neben den Logs, dazu eine Beidatei mit den
Thread-Namen aus `/proc`. Das `alloctool` im Repository macht daraus Bytes je Thread, je Typ, je
innerster Methode außerhalb der Laufzeit und die häufigsten Stapel je Typ; sein `selftest`
zeichnet den eigenen Prozess auf und findet eine Churn-Stelle bekannten Namens. Die
Client-Bibliothek der Diagnose (rein verwaltet) liegt im Zip neben `Komet.dll`.

Was damit zu tun ist, steht in der Starter-Datei des Nutzers: Server-GC (721-ms-Pause) und ein
großes gen0 (Einzelpausen 40–58 ms) sind beide gemessen und verworfen. Was bleibt, ist weniger
Müll an der Quelle — und dafür braucht es die Stelle, nicht den Typ.

### Zwei Fehler, die der erste Blick auf den Bildschirm fand (06.09.)

**Das Gelände verschwand.** `TesselatedChunkPart.AddToPools` ruft als letzte Anweisung
`Dispose()` auf dem Teil auf. Mein Postfix auf `Dispose` — der Aufräumweg für ein Teil, das nie
in die Pools gelangt — lief damit *innerhalb* von `AddToPools`, vor dessen eigenem Postfix, und
recycelte die beiden verschmolzenen Meshes, während das Engine-Mesh diese Flächen bereits
verloren hatte. Jede verschmelzbare Oberfläche der Welt fehlte; übrig blieben Grasbüschel,
Blumen und Bäume über einer weißen Leere. Die Übergabe geschieht jetzt im **Prefix**: die
Meshes liegen im `__state` dieses Aufrufs, bevor die Engine irgendetwas disposen kann. Nichts
daran hängt mehr an der Reihenfolge, in der Harmony zwei Patches ausführt. Die Prüfung dazu
fährt die echten Engine-Methoden in der echten Reihenfolge und wurde gegengeprüft, indem der
Fix verkrüppelt wurde — dann schlägt sie an.

**Und es hätte ohnehin nichts gezeichnet.** Die Vorgabe-Entfernung war die Fern-LOD-Grenze der
Engine, also `min(640, Sichtweite) × lodBiasFar`. Bei Sichtweite 512 und `lodBiasFar 1,0` liegt
die genau *auf* der Sichtweite: das Band, in dem ein Rechteck gezeichnet würde, ist leer. Die
Vorgabe ist jetzt diese Grenze, gedeckelt auf sechs Zehntel der Sichtweite, geprüft über fünf
Kombinationen aus Sichtweite und Bias.

**Ein Wachhund.** Verschmelzen ohne Platzieren ist der eine Fehlerfall, der sich nicht ankündigt
— die Flächen sind aus dem Engine-Mesh heraus, und ob etwas an ihre Stelle trat, sieht man erst
am Loch. Werden 24 Teile verschmolzen und keines platziert, schaltet sich das Feature ab, sagt
es im Log und im Report, und verweist auf `.komet retess`.

## Die Antwort der Messung: das Fern-Mesh scheitert an der Rauheit, und der Schatten ist der Berg (06.09.)

### Fern-Mesh: 1,3 Flächen je Rechteck

Der Report mit Instrumentierung, an derselben Stelle:

| Rechteckgröße | 1 | 2 | 3-4 | 5-8 | 9+ |
|---|---|---|---|---|---|
| Anteil | 83 % | 12 % | 4 % | 1 % | 0 % |

97 % der Flächen erfüllen die Regeln, und 23 % von ihnen verschwinden. Die Zeile, die das
erklärt: **von den Einzelflächen wurden 92 % dadurch gestoppt, dass in der Nachbarzelle
überhaupt keine Fläche derselben Gruppe lag** — eine andere Ebene oder eine andere Kachel. Das
ist gewachsenes Gelände, das von Block zu Block die Höhe wechselt. Keine Toleranz reicht dorthin;
die Lichtregeln machten 7 % aus, die geteilte Kante 0 %.

Verschmelzen setzt Koplanarität voraus. Auf einer Heightmap mit Blockrauheit gibt es keine
großen koplanaren Flächen. Der Ansatz kann auf natürlichem Boden nicht gewinnen, und das ist
kein Implementierungsdetail, sondern die Geometrie. Gespart wurden 6 % der Kamera-Dreiecke,
bezahlt mit 12.130 zusätzlichen Pool-Teilen von 27.400 und 0,28 ms je Teil auf dem
Tesselations-Thread. **Per Default aus, Layout 23.** Der Code bleibt: auf gebauten, flachen
Strukturen ist das Verhältnis ein anderes, und die Maschinerie ist genau die, die ein echtes
Fern-LOD bräuchte.

### Und dann der eigentliche Fund

Ein Umschalten, `.komet toggle shadowfoliage`, an derselben Stelle:

| | Frame | GPU | nahes Laub | fernes Laub | Kamera opaque |
|---|---|---|---|---|---|
| mit Laub im Schatten | 6,27 ms | 5,18 ms | 1,8 ms / 315 Mfrag | 3,0 ms / 250 Mfrag | 1,6 ms / 32 Mfrag |
| ohne | 4,40 ms | 3,80 ms | 0 | 0 | 1,9 ms / 32 Mfrag |

**160 auf 228 fps. Das Laub in den beiden Schattenkarten ist 30 % der Frame-Zeit.** Auf jedes
Fragment im Kamerabild kommen elf im Schattenlaub. Der ganze Sommer der Optimierung am
Kamera-Pass — Reihenfolge, Pools, Verschmelzen — bewegte sich um einen Posten von 1,6 ms,
während 1,9 ms unbeachtet in den Schattenkarten lagen.

Warum so viel: die nahe Karte hat 4096 Pixel für 39 Blöcke, also 80 Texel je Block; bei 17 Grad
Sonnenhöhe steht die Projektion flach, und jeder Grasbüschel wird mit Alpha-Test hineingezeichnet
— 19-fache Überzeichnung. Die ferne Karte deckt 255 Blöcke mit 7168 Pixeln.

### Der Hebel: eine Reichweite für werfendes Laub

Schattenkarten sind orthographisch. Ein Grasbüschel kostet in 250 Blöcken Entfernung genau so
viele Schatten-Texel wie in 20 — die Fragmente skalieren mit der **Fläche**, die die Kaskade
deckt, nicht mit der Entfernung des Werfers. Die ferne Kaskade von 255 auf 100 Blöcke zu
beschneiden lässt 15 % ihrer Laub-Fragmente übrig.

`ShadowFoliageRange` (`.komet shadowfoliagerange <blocks>`) verengt dafür das achsenparallele
Band des Sweeps — denselben Test, den die Engine macht, nur enger — und zwar für die
Laub-Pässe und keinen anderen. Die Prüfung fährt beide Richtungen: ein solider Pass darf nie
verengt werden (das wären Löcher im Schatten), und keiner darf je geweitet werden.

**Per Default aus.** Blätter und Grasbüschel liegen im selben Render-Pass und sind auf
Pool-Granularität nicht zu trennen; eine zu kurze Reichweite nimmt einem fernen Wald die
Eigenverschattung. Das ist sichtbar, und diese Entscheidung gehört dem Nutzer, nicht der
Vorgabe.

## Korrektur und ein neuer Verdächtiger (06.09., zweite Szene)

### Der Schattenlaub-Fund war ortsabhängig

Dieselbe Sonde, andere Stelle, andere Tageszeit:

| | Sonnenhöhe | Nahband | nahes Laub | Kamera opaque |
|---|---|---|---|---|
| Sumpf, Sichtweite 512 | 17 Grad | 224 x 55 Blöcke | 1,8 ms / 315 Mfrag | 1,6 ms / 32 Mfrag |
| Ebene, große Sichtweite | 61 Grad | 121 x 55 Blöcke | 0,5 ms / 43 Mfrag | 10,7 ms / 48 Mfrag |

Bei flacher Sonne zieht sich die Box der nahen Kaskade in der Welt lang, und ihr Laub wird zum
größten Posten des Frames. Bei hoher Sonne ist sie kompakt und kostet fast nichts. **Der Fund
gilt für flache Sonne und kurze Sichtweite, nicht allgemein.** Die Reichweite bleibt ein Regler,
keine Vorgabe. Der Report nennt jetzt die Dreiecke beider Kaskaden, damit ihre Wirkung in einem
Befehl sichtbar ist statt in einer Minute Mittelung.

### 10,43 ms für 48 Kilobyte

Der Report der zweiten Szene, CPU-gebunden bei 68 % GPU-Auslastung:

```
particles: 1.543 alive on the main pools (10,91 ms/frame: physics 0,48 + upload 10,43)
```

Die Partikel-Pools schreiben je Frame drei Instanz-Puffer: Flags, CustomFloats, CustomBytes.
Bei 1.543 lebenden Partikeln sind das 6, 24 und 18 Kilobyte. 48 KB in 10,43 ms wäre eine
Übertragungsrate von 4,6 MB/s — das ist keine Übertragung, das ist Warten.

`glBufferSubData` auf einen Puffer, den die GPU noch liest, hat zwei Möglichkeiten: bis zum
Ende des betreffenden Draws blockieren, oder den Treiber eine Schattenkopie anlegen lassen. Die
Instanz-Puffer der Partikel werden in jedem Frame neu geschrieben und in jedem Frame gezeichnet,
also genau der Fall, der blockiert.

`glInvalidateBufferData` sagt „der alte Inhalt spielt keine Rolle mehr". Der Treiber vergibt
dann neuen Speicher, statt zu synchronisieren — der Puffer wird umbenannt, nicht abgewartet. Das
ist hier aus einem nennbaren Grund sicher: der Pool schreibt `AliveCount` Instanzen, und der
Draw-Aufruf liest genau `AliveCount`. Was die Invalidierung verwirft, liest nie jemand.

Per Default aus, bis ein Report es bepreist. `.komet toggle particleorphan` macht den A/B in
einem Befehl, und die Partikel-Zeile sagt, welchen Weg sie gerade nimmt.

---

## Drei Posten am Ladepfad, und einer davon war messbar (06.09., Aufräumrunde)

Der Engpass beim Laden ist unverändert der eine Tesselations-Thread. Diese Runde nimmt ihm
Arbeit ab, die er gar nicht tun müsste, und nimmt dem Lock, das er dauernd braucht, Verkehr weg.

### Der Rand-Sweep hat die halbe Warteschlange durchgehasht

Der Sweep für die Randreparaturen (`EdgeRetessPriorityPatches`) rotiert `dirtyChunks` alle
50 ms einmal komplett — auf dem Tesselations-Thread, unter `dirtyChunksLock`. Eine Rotation
über die öffentliche API von `UniqueQueue` kostet **vier Hash-Operationen je Schlüssel**:
`Dequeue` nimmt ihn aus dem `HashSet`, `Enqueue` legt ihn sofort wieder hinein. Für Schlüssel,
die die Queue überhaupt nicht verlassen. Und die Queue, um die es geht, hält bei genau der
Chunk-Flut, für die der Sweep gebaut wurde, Zehntausende davon.

Jetzt rotiert der Sweep die *innere* `Queue` und fasst das Set nur für die höchstens 64
Schlüssel an, die wirklich befördert werden. Gleiche Queue, gleiche Reihenfolge, gleiches
Ergebnis. Gemessen (`./build.sh bench`, Abschnitt `edge sweep rotation`):

```
     backlog   via UniqueQueue   inner queue   speedup    saved per second
         200           0,013ms       0,003ms     4,66x             0,21 ms/s
        2000           0,114ms       0,023ms     4,87x             1,82 ms/s
       12000           0,624ms       0,111ms     5,60x            10,25 ms/s
       45000           1,107ms       0,183ms     6,07x            18,50 ms/s
```

Vier Läufe, einer davon oben. Die tiefen Zeilen streuen (der erste Lauf zeigte für 45.000
einmal 2,08 ms und damit 10,9x — ein Ausreißer, vermutlich ein Wachstumsschritt des HashSet);
belastbar sind **4,6–5,1x** oben und **5,8–6,8x** unten. Die 45.000 sind die Warteschlange,
gegen die die Zuflussbremse gebaut wurde. Dort ist das knapp 1 ms je Sweep weniger auf dem
Tesselations-Thread — und dieselbe Verkürzung der Haltezeit von `dirtyChunksLock`, unter dem
der Netz-Thread jeden ankommenden Chunk einträgt.

Der API-Weg bleibt als Rückfall, falls ein Spiel-Update die beiden Felder verschiebt.
`verify` fährt **beide** Wege durch dieselben Zusicherungen — Reihenfolge, Erhaltungs-Fuzz, und
neu: Set und Queue müssen hinterher übereinstimmen (`Count` liest das Set, der Enumerator die
Queue). Gegengeprüft, indem das `set.Remove` aus dem schnellen Weg entfernt wurde: der Test
schlägt an.

### Der Nachbar-Prefetcher hat dieselben 32 Einträge dreihundertmal die Sekunde abgelaufen

Der Prefetcher schaut 32 Queue-Einträge voraus und entpackt die 27 Nachbarn jedes Eintrags,
schläft 2 ms und fängt von vorn an. Der Tesselator verbraucht in 2 ms **weniger als einen**
Chunk — zwei aufeinanderfolgende Schnappschüsse sind also praktisch identisch. Fast jeder
Durchgang waren damit 32 `chunksLock`-Nahmen und rund 860 Dictionary-Zugriffe für Chunks, die
der vorige Durchgang schon entpackt hatte. `chunksLock` ist nicht irgendein Lock: der
Tesselations-Thread nimmt es für jede Nachbarschaft, die er liest, der Netz-Thread für jeden
Chunk, der ankommt.

Er merkt sich jetzt die Einträge, die er abgelaufen ist, und schläft 20 ms statt 2, wenn ein
Durchgang nichts Neues fand — 32 Einträge Vorlauf sind bei ~4 ms je Chunk über hundert
Millisekunden, ein längeres Nickerchen kann den Vorlauf nicht leerlaufen lassen. Das Set gehört
dem Worker allein: ein Weltwechsel erhöht eine Epoche, die er beim nächsten Durchgang selbst
abräumt, statt dass ein fremder Thread ein `HashSet` unter einem laufenden `Add` löscht.
Danebenliegen kostet weiterhin nur Arbeit, die ohnehin angefallen wäre — ein Chunk, den der
Pool nach dem Überspringen wieder packt, wird vom Tesselator entpackt, genau wie vor diesem
Worker.

### Die Fenster-Vorhersage zeigte regelmäßig auf einen Chunk, der nie gemesht wird

Der Fenster-Prebuilder sagte den vordersten Queue-Eintrag voraus. Genau der ist aber
regelmäßig einer, den `TesselateChunk` fallen lässt, *bevor* es je ein Fenster baut: ein Chunk,
der fehlt, der leer ist (bei großer Sichtweite ist der halbe Chunk-Turm über dem Boden Luft),
oder der noch nicht vom Server geladen ist. Eine solche Vorhersage kostet den Vorlauf doppelt:
der Worker baut nichts (`BuildWindow` bricht bei leerer Mitte ab), und der Chunk, den der
Tesselator wirklich erreicht, zahlt den vollen Fensterbau von ~1,2 ms.

Die Vorhersage überspringt jetzt bis zu acht solcher Einträge, in **einer** Nahme von
`chunksLock`, und schlägt den Schlüssel direkt in der Chunk-Map nach: der Queue-Schlüssel *ist*
der Chunk-Schlüssel — `SetChunkDirty` schlägt den Chunk damit nach, bevor es ihn einreiht — und
beide Markierungs-Trichter stehen im Engine-Fingerprint, ein Umbau daran fällt also im
Drift-Check auf statt still danebenzuliegen. Falsch liegen ist in beide Richtungen umsonst: ein
Chunk, der zwischen Vorhersage und Pop aufhört leer zu sein, lässt den Tesselator sein Fenster
selbst bauen — wie vor jeder Vorhersage. Die Report-Zeile `window pipeline` zählt die
übersprungenen Einträge, damit „bringt das hier etwas" eine Zahl hat und keine Behauptung ist.

### Und was dabei bewusst liegen blieb

`ChunkTesselatorManager.OnBeforeFrame` lädt die fertigen Meshes **unter**
`tessChunksQueueLock` hoch — demselben Lock, das der Tesselations-Thread am Ende jedes Chunks
für `EnqueueOrMerge` braucht. Das sieht nach Lock-Kontention aus, bis man die eigene Messung
liest: `warteschl. 1585/5` — 1585 Chunks warten auf die Tesselation, **fünf** auf den Upload.
Die Queue ist kurz, weil die Tesselation der Engpass ist, also wird das Lock kaum gehalten. Ein
Umbau von `OnBeforeFrame` wäre ein Umbau eines funktionierenden Systems ohne messbaren Gewinn.

Ebenso `CalculateVisibleFaces` und `CalculateVisibleFaces_Fluids`: sie hängen nur am Fenster,
nicht am Meshing des vorigen Chunks, könnten also auf denselben Worker wie der Fensterbau. Sie
rufen aber `AllowSnowCoverage`, `ShouldMergeFace` und `SideIsSolid` auf — virtuelle Methoden
beliebiger `Block`-Unterklassen, also auch fremder Mods — und benutzen `tmpPos`, ein Feld des
einen `ChunkTesselator`. Das ist der nächste greifbare Posten am Ladepfad, aber er braucht
dieselbe In-Game-Validierung, die der Fensterbau bekommen hat, nicht bloß ein Argument.

### Das Schalter-Fenster passte nicht in seinen Rahmen

Zwei unabhängige Überläufe auf der `.komet`-Seite mit den Schaltern, beide daher, dass die
Seite für die *Entwurfsgröße* gesetzt und in *irgendeiner* Größe gezeichnet wurde.

Nach unten: dreizehn Schalter in festem 32er-Raster fangen 44 unter der Oberkante an und enden
460 tiefer; der Boden des Inhaltsfelds liegt bei 443. Bei kleinem Fenster oder hoher
GUI-Skalierung wurden die letzten Schalter und die komplette Meldungszeile *unter* den Rahmen
gezeichnet. Raster, Schaltergröße und Panelhöhe kommen jetzt aus dem Platz, der da ist, und die
acht Gruppenknöpfe brechen auf so viele Zeilen um, wie ihre längste Beschriftung braucht,
statt rechts hinauszulaufen.

Nach rechts: ein Schalter, der auf dieser Maschine nicht umlegbar ist, hängt seinen Grund an
die Zeile — und die Zeile ging an das statische Textelement der Engine, das auf die
Feld*breite* umbricht und dann über die Feld*höhe* hinaus weiterzeichnet. Ein satzlanger Grund
wurde quer über die Beschriftung des nächsten Schalters gemalt. Eine Zeile ist jetzt eine
Zeile, auf die Zellen ihres Feldes gekürzt; der ganze Satz steht einen Klick entfernt im Panel
darunter, wo er ohnehin immer landete und korrekt umbricht.

`verify` prüft jede Gruppe in jeder Fenstergröße der bestehenden Layout-Prüfung, mal vier
Knopfbreiten, wie sie eine Übersetzung erzeugen kann: Knöpfe im Inhalt, Schalter nicht in die
Zeile darunter ragend, Beschriftungen in ihren Feldern, Panel weder über der letzten Zeile noch
aus dem Rahmen — und nie unter lesbare Höhe geschrumpft. Es waren genau die Schalterzeilen, die
der bestehenden Prüfung „jede Seite passt in ihr Panel" entgangen sind: sie werden als
Elemente komponiert, nicht von `TextPanel` gerastert.

---

## Ein Worker-Pool statt vier Thread-Sätzen, die nichts voneinander wussten (06.09.)

Bis hierher hielt Komet **vier unabhängige Thread-Sätze**: fünf Cull-Helfer, vier
Occlusion-Helfer, einen eigenen Thread für den Fenster-Vorbau und einen für den
Nachbar-Prefetch — dazu Animations-Vorbau und HUD-Raster auf dem geteilten .NET-ThreadPool.
Jeder Satz bemaß sich an der Kernzahl, ohne von den anderen zu wissen: **elf Threads auf sechs
physischen Kernen**, neben dem Render-Thread, dem Tesselations-Thread der Engine und den
Worldgen-Threads des eingebauten Servers.

Keiner konnte einem anderen einen Thread leihen. Und die beiden, auf die es ankommt — der Sweep
auf der Frame-Deadline und der Occlusion-Walk, der Kerne millisekundenlang hält — kollidierten
oft genug, dass dafür eigens ein Niceness-Mechanismus gebaut wurde.

### Was der Pool ist

`JobScheduler` ersetzt alles davon. Ein Worker ist **keiner Arbeitslast zugeordnet**; er nimmt
den wertvollsten wartenden Job. Zwei Formen teilen sich den Pool:

* **Fork/Join** (`RunBatch`), der Aufrufer blockiert: der Sweep als `Critical`, der
  Occlusion-Walk als `Background`. Die Scheiben werden über einen Interlocked-Zähler dynamisch
  vergeben, der Aufrufer arbeitet mit, und **die Fertigstellung wird in *Arbeit* gezählt, nicht
  in Workern** — ein Helfer, der nie aufgewacht ist, kann niemanden aufhalten. Das ist
  unverändert die Lehre aus dem 01.09.-Log (9,7–11 ms Sweep-Warten ohne GC-Pause).
* **Fire-and-forget** (`Submit`) mit Dedup-Schlüssel: Fenster-Vorbau (`High`), Nachbar-Unpack
  (`Normal`), HUD-Raster (`Background`), Animations-Vorbau (`Idle`).

Beide Batch-Lasten waren ohnehin auf **Zehntel-Millisekunden-Scheiben** geschnitten (Occlusion
64 Positionen ≈ 30 µs, Cull acht Scheiben je Worker ≈ 15 µs). Ein `Critical`-Job wartet also
etwa so lange, wie ein OS-Wecken ohnehin kostet — das ist die Messung, die den geteilten Pool
überhaupt zulässig macht.

### Gemessen

`./build.sh bench`, fünf Läufe je Konfiguration, Median der Sweep-Kosten pro Frame:

```
4 Pool-Worker   0,703  0,711  0,700  0,702   -> 0,70 ms
5 Pool-Worker   0,701  0,693  0,825  0,989   -> 0,70-0,83 ms
alter Zustand (5 dedizierte Cull-Helfer)     -> 0,77 ms
```

Dieselbe Arbeit auf **weniger** Threads, nicht langsamer. Genau darum geht es: Die elf Threads
waren kein Durchsatz, sie waren Überbuchung.

### Die Regeln, und warum sie so lauten

**Ein Batch-Ticket wird nie storniert.** `CancelKind` ist für Fire-and-forget-Arbeit, deren
Welt weggegangen ist. Ein Fork/Join-Aufrufer ist das nicht: er hängt in diesem Moment an seinen
Scheiben. Die erste Fassung stornierte auch die — der Stresstest lieferte daraufhin Batches mit
Scheiben, die stillschweigend nie gelaufen waren. Ein halb gecullter Frame ist schlimmer als
ein später.

**Jeder N-te Zugriff fängt unten an.** Strikte Priorität ließe den Animations-Vorbau hinter
einem Sweep verhungern, der dreimal pro Frame feuert. Ein Zähler, mehr kostet es nicht, und die
Wartezeit ganz unten ist damit auf N Zugriffe begrenzt statt auf „wenn die Maschine mal ruhig
ist".

**Der Dedup-Schlüssel ersetzt das handgeschriebene „schon gesehen".** Ein Chunk, der eingereiht
ist oder gerade läuft, wird nicht noch einmal eingereiht — genau die Eigenschaft, für die der
Prefetcher vorher ein eigenes `HashSet` mit Epochenzähler brauchte.

**Niceness bleibt eine Einbahnstraße.** `setpriority` darf ein unprivilegierter Thread nur
*erhöhen*, nie zurücknehmen. Ein Worker, der nice geworden ist, kann die Frame-Deadline nicht
mehr bedienen — deshalb lehnen genau diese Worker die beiden obersten Queues ab. Bei
`OcclusionThreadNiceness = 0` (Default) ist der Pool vollständig symmetrisch.

### Wie viele Worker

Die Obergrenze ist **physische Kerne minus zwei** — einer für den Render-Thread, einer für den
Tesselator, die beiden, denen dieser Pool nie einen Kern wegnehmen darf — gedeckelt bei acht,
Boden eins. Hardware-Threads werden bewusst nicht gezählt: beide Batch-Lasten sind
speichergebundene lineare Scans, ein SMT-Geschwister bringt Warteschlange statt Durchsatz.

Von dort regelt der Pool selbst: einen Worker zurück, wenn ein Frame über dem 1,5-fachen des
gleitenden Mittels lag *und* der Pool beschäftigt war; wieder her, wenn der Tesselations-Rückstand
sagt, dass die Jobs des Pools auf dem kritischen Pfad dessen liegen, worauf der Spieler wartet;
auf eins herunter, wenn nichts wartet. Die GC-Pause wird vorher abgezogen — aus demselben Grund
wie bei der Upload-Drossel: eine Pause friert *alle* Threads ein und ist kein Beleg dafür, dass
der Pool dem Render-Thread Kerne wegnimmt. Threads werden dafür nie erzeugt oder zerstört; wer
über dem Ziel liegt, parkt.

### Der Monitor

`.komet` → Threads/Jobs zeigt Worker (beschäftigt/wach/untätig, Auslastung), Schlangenlänge,
Jobs/s, fertig/verworfen/doppelt, die Wartezeit des Aufrufers je Batch und die
Hauptthread-Übergabe — dann **eine Zeile je Worker** mit Zustand, Chunk und Laufzeit, dazu eine
Aufschlüsselung je Arbeitslast. Er liest flüchtige Felder ohne Lock: eine Zeile, die einen Job
alt ist, ist der richtige Preis für einen Monitor, der den Pool nichts kostet.

**Es gibt bewusst kein `GENERATING`, `TESSELLATING` oder `UPLOADING`.** Chunk-Erzeugung sind die
Worldgen-Threads des Servers, Chunk-Laden ist der Netz-Thread plus Hauptthread-Tasks, und
Tesselation, Meshing und der GPU-Upload sind Engine-Threads, die keine Mod einplanen kann — der
Tesselator, weil `BlockEntity.OnTesselation` ein öffentlicher Erweiterungspunkt ist, den jede
Content-Mod gegen einen Single-Thread-Vertrag implementiert, der Upload wegen des GL-Kontexts.
Solche Zustände zu erfinden wäre eine Lüge an genau der Stelle, an der man nachsieht, wohin die
Zeit gegangen ist.



## Fern-LOD: jenseits der Distanz Zellen statt Blöcke (06.09., abends)

Zwei Reports von derselben Stelle, eine Minute auseinander:

| | Frame | Dreiecke Kamera-Pass | davon 640+ | GPU opaque |
|---|---|---|---|---|
| Blick auf den Boden | 5,3 ms = 189 fps | 125.000 | 0 | 1,0 ms / 13 Mfrag |
| Blick zum Horizont, Sichtweite 1536 | 19,0 ms = 53 fps | 16,1 Mio. | 12,7 Mio. | 11,7 ms / 15 Mfrag |

Die CPU war es nicht: Sweep 2,2 ms auf fünf Threads, Tick 0,3 ms, Upload 0,2 ms, die 14,8 ms
„opaque" der CPU sind das Warten auf die GPU. Die GPU-Füllrate war es auch nicht: 15 Mio.
Fragmente auf 3,7 Mio. Pixel, dieselbe Größenordnung wie beim Boden-Blick. Und die reine
Primitiv-Rate auch nicht — 1,4 Mrd. Dreiecke/s ist für eine RX 9070 XT wenig. Was übrig bleibt,
ist das Front-End: `chunkopaque.vsh` exportiert rund dreizehn vec4 je Vertex (Position, uv,
Fog, Normale, Weltposition, Kameraposition, zwei Schattenkoordinaten, zwei Colormap-uvs, …)
und rechnet bis zu acht 3D-Value-Noise-Aufrufe (Saison, Frost, Wind), und jedes
Sub-Pixel-Dreieck zahlt das für zwei Vertices. Der einzige Hebel ist: weniger Dreiecke dort
draußen. Das Verschmelzen koplanarer Flächen (vorheriger Abschnitt) hatte gezeigt, dass es die
nicht liefern kann — 1,3 Flächen je Rechteck auf gewachsenem Gelände.

### Downsampling braucht keine Koplanarität

`Runtime/FarLod.cs` baut aus dem, was die Engine tesseliert hat, das Bild eines Chunks in
Zellen von 2×2×2 Blöcken:

* Eine **Einheitsfläche** (achsenparallel, genau eine Einheit in beiden Ebenenrichtungen, an
  ganzzahligen Positionen, mit gepackter Vertex-Normale entlang der konstanten Achse) markiert
  den Block hinter sich als **fest** und den davor als **Luft**. Alles andere — Graskreuze,
  Blätterwürfel (um y gedreht, also nie achsenparallel), Treppen, Zäune, Platten, Meißelblöcke —
  ist eine **Restfläche**, zugeordnet dem Block, in dem ihr Schwerpunkt liegt.
* Luft **flutet** von den bekannten Luftblöcken durch das Unbekannte, nur innerhalb des Chunks
  und eine Reihe nach oben in den Rand (eine Zelle am Chunk-Dach ist bis zu einen Block dicker
  als ihre Blöcke; ihre Oberseite braucht darüber Luft). Was die Flut nicht erreicht, ist
  verschüttet. Der seitliche Rand steht für die Nachbarchunks, über die die Flächen genau eines
  sagen: der Block vor einer Fläche ist Luft. Die Flut dort hindurch laufen zu lassen, hieße,
  das Gelände des Nachbarn überall dort Luft zu nennen, wo der Himmel die Chunkgrenze berührt —
  jede Randzelle bekäme eine Seitenfläche in den Nachbarn hinein, unsichtbar und ein Drittel
  mehr Dreiecke.
* Eine **Zelle** ist fest, wenn einer ihrer Blöcke fest ist, sonst Luft, wenn einer Luft ist,
  sonst verschüttet. Das Bild ist also nie dünner als die Welt, nur bis zu einen Block dicker —
  deshalb klaffen zwischen Nachbarchunks auf verschiedenen Stufen keine Lücken.
* Eine feste Zelle bekommt je Luft-Nachbarn **eine Fläche**, die die äußerste Quellfläche dieser
  Richtung in der Zelle kopiert: Kachel, die vier Vertex-Lichter, Flags, Colormap-Daten,
  Gras-uv, Indexmuster und Eckenreihenfolge. Winding und SSBO-Face-Packung tragen sich durch,
  kein Shader wird angefasst; die Kachel liegt über zwei Blöcke gespannt, was in der Entfernung,
  in der die Zelle gezeichnet wird, ohnehin der Mip-Mittelwert ist.
* Jede Zelle behält höchstens **einen Restblock**: den mit den meisten Flächen, um zwei
  skaliert um die Zellecke in x und z und um den Zellboden in y — ein Gras auf der oberen
  Zellhälfte steht damit auf der dicker gewordenen Zelloberseite statt darin. Ein doppelt so
  großes Gras, wo vier standen, ist bei vier Pixeln dasselbe Grün.

**Stufe 2** ist derselbe Build auf der Ausgabe von Stufe 1 mit Einheit 2: Zellen von vier
Blöcken. Gemessen (`./build.sh bench`, Zeile `far lod build`) an einem Chunk mit 6.462 Flächen —
Grasoberseiten, Erdseiten mit Grat, Gras auf zwei Fünfteln der Spalten, vier Bäume: **1.820
Flächen auf Stufe 1 (3,6×), 654 auf Stufe 2 (9,9×), beide Stufen zusammen etwa 1,2 ms** auf
dem Tesselations-Thread. Davon Klassifikation 0,5, Flut 0,2, Emission 0,25.

### Die Anbindung: vier LOD-Stufen, die die Engine nie vergibt

`FarMeshPatches` (der Name blieb, die Schalter heißen weiter `FarMesh…`) hakt sich an denselben
drei Stellen ein wie das Verschmelzen: Postfix auf `NowProcessChunk` (Tesselations-Thread,
beide Stufen bauen), Präfix/Postfix um `AddToPools` (Übergabe im Präfix, weil der Engine-Rumpf
mit `Dispose` endet — siehe oben), Präfix auf `RenderOpaque` für den Modus-Abgleich. Neu ist,
dass **die Engine-Meshes unangetastet bleiben**: ein Teil mit Bild wird nur umgestuft.

| Stufe | was | gezeichnet |
|---|---|---|
| 5 | das LOD-1-Mesh der Engine, wenn das Teil ein Bild hat | bis zur Distanz D |
| 4 | Stufe-1-Bild | D bis 2D |
| 6 | Stufe-2-Bild | jenseits 2D |
| 7 | Stufe-1-Bild ohne Stufe-2-Geschwister | D bis Sichtweite |

`InFrustumAndRange` der Engine liefert für alle vier `false` — das Sicherheitsnetz: ist der
Sweep nicht unser oder das Feature aus, setzt `SyncMode` die Stufe-5-Teile auf 1 zurück und
versteckt die Bilder. Die Schattenpässe zeichnen nur die Engine-Meshes (5 wirft wie 1, die
Bilder nie): ein Bild ist bis zu einen Block dicker als die Welt, und das sähe man als Schatten
vor den Füßen. D = `max(400, 0,35 × Sichtweite)` (538 bei 1536: 88 % der sichtbaren Fläche
liegen dahinter), `.komet farmesh <Blöcke>` verschiebt es live, `FarMeshTier2` bzw. `.komet
toggle farlod2` schaltet die zweite Stufe.

**Stufe 2 und der Rand-Retess.** Die Engine tesseliert die Zwei-Block-Schale eines Chunks
allein neu, wenn ein Nachbar sich ändert; Zellen von vier liegen über Schale und Mitte. Stufe 2
wird deshalb aus Mitte und Schale gebaut und **hängt an der Location-Liste des ersten
Mittelteils**; ein Schalen-Retess lässt sie stehen (bei vierfacher Distanz ist ein geänderter
Block in der Schale nichts, was jemand sieht) und stuft seine neuen Stufe-1-Bilder so ein, dass
sie enden, wo Stufe 2 beginnt (ein Register je Chunkposition sagt ihm, dass es eine gibt). Ein
Chunk ohne Mittelteil hält Stufe 2 an der Schale und baut sie mit ihr neu. Im Schalen-Build
gilt die Mitte als unbekannt, und die Flut betritt sie nicht — sonst bekäme die Schale Flächen
gegen das Bild der Mitte, das noch in den Pools steht.

**Wachhund.** Bauen ohne Platzieren hieße: die Engine-Meshes enden an der Distanz und dahinter
steht nichts — die Welt endete bei D. Also zählt der Postfix Übergaben und Platzierungen, und
ab 24 Übergaben ohne Bild schaltet sich das Feature ab und sagt es im Log und Report.

### Geprüft, ohne Spiel

Ein 16×16-Plateau wird exakt zu 8×8 Zelloberseiten mit Kachel, Licht, Flags und Indexmuster der
Quelle und ohne erfundene Randwand; zwölf zufällige Wellengelände (mit Grat und Klippen) ergeben
**exakt** das Zellbild, das sich aus der Heightmap allein berechnet — auf beiden Stufen, Ober-
seiten im TopSoil-Teil, Seiten im Opaque-Teil, jede Oberseite von der höchsten Spalte ihrer
Zelle; Gras kommt je Zelle einmal heraus, verdoppelt, auf der Zelloberseite stehend; ein
Schalen-Build erfindet keine Fläche gegen die Mitte, ein voller Build flutet sie; die Übergabe
übersteht die Dispose-Reihenfolge der Engine, und die Stufen 4/5/6/7 kommen so heraus, wie die
Hosting-Regeln es sagen; der Sweep zeichnet jede Stufe in ihrem Band und in keinem Schattenpass
ein Bild, und der Cull-Verifier stimmt zu.

### Was der nächste Report sagen muss

Die Zeile `far lod` (Flächen rein/raus je Build, ms je Chunk, Bilder in den Pools, Dreiecke je
Frame als Stufe 1, Stufe 2, Engine-innerhalb) und `camera pass by lod`. Erwartung aus den
Zahlen: die 12,7 Mio. jenseits 640 fallen auf ein Drittel bis ein Viertel, die 4,4 Mio. im Band
211–640 zur Hälfte (D liegt bei 538); Frame beim Horizontblick von 19 auf 9–11 ms. Was man
anschauen muss: die Kante bei D (ein Block Versatz, drei Pixel), Wälder (Blätterwürfel je Zelle
einer, doppelt groß), Gebäude jenseits D (ein Repräsentant je Zelle bei Treppen und Zäunen).

### Was liegen blieb

* **Der Build auf einem Worker statt dem Tesselations-Thread.** 1,2 ms auf 2,4 ms Tesselation
  sind ein Drittel weniger Chunk-Durchsatz beim Streamen. Auslagern ginge (Job je Chunk,
  Präfix von `AddToPools` wartet, falls der Job noch läuft), verlangt aber, dass die
  Engine-Meshes bis dahin unangetastet bleiben — `MergeIfEqual` disposed Teile auf dem
  Tesselations-Thread, während ein Worker sie liest. Erst, wenn die Report-Zeile zeigt, dass
  es nötig ist.
* Liquid, Transparent, Meta, Decor bekommen kein Bild (Liquid bräuchte die CustomFloats des
  Liquid-Passes); sie werden wie bisher in jeder Entfernung gezeichnet.
* Eine Zellfläche, für die es in der Zelle keine Quellfläche der Richtung gibt (ein
  Treppenrücken, der die Nachbarfläche wegkullt), wird ausgelassen und gezählt (`cell faces
  without a source face`).

## Der Feldtest: das Fern-LOD wirkt, und drei Posten fressen den Gewinn (06.09., nachts)

Zwei Reports von derselben Stelle, anderthalb Stunden nach den beiden oben:

| Blick zum Horizont | vorher | mit Fern-LOD |
|---|---|---|
| Frame | 19,01 ms = 53 fps | 8,85 ms = 113 fps |
| Kamera-Pass Dreiecke | 16,1 Mio. | 6,9 Mio. |
| GPU-Frame | 14,89 ms | 6,95 ms |
| Kamera-Opaque (Elapsed-Sonde) | 11,7 ms / 15 Mfrag | 4,2 ms / 30 Mfrag |

Und derselbe Blick auf den Boden: **189 → 170 fps**. Der Nutzer sagte „hat nichts gebracht" —
das Gefühl kam von den Rucklern: **152/min statt 101/min, 241 von 276 mit GC-Pause**, einzelne
gen0/gen1-Pausen bis 44 ms. Die Mittelwerte sagen das Gegenteil, das Spielgefühl folgt aber den
Rucklern. Drei Posten waren dafür verantwortlich, alle drei jetzt behoben.

### 1. Die Bilder lagen in denselben Pools wie die Engine-Meshes

`AddModel` nimmt den ersten Pool mit Platz. Also lag jedes Bild im Indexpuffer **zwischen** zwei
Engine-Teilen — und diesseits der Distanz ist ein Bild unsichtbar, das Engine-Teil sichtbar.
Jedes unsichtbare Teil zerschneidet einen Lauf, den der Sweep sonst zu einer Range verschmilzt:
emittierte Ranges je Roh-Range fielen beim Bodenblick von **3,2 auf 1,6**, Draw-Calls stiegen
von 454 auf 812. Und weil jeder Pool in jeder Ansicht irgendetwas Sichtbares enthielt, wurde
jeder Pool gezeichnet.

`SpatialPools` hatte das Routing schon — nach Region, per Default aus. Es bekommt jetzt
**Lanes**: `SpatialPools.Lane` wird um genau einen `AddModel`-Aufruf gesetzt und schickt das
Modell in einen Pool-Satz, in den sonst nichts kommt. Drei Lanes: Engine-Meshes (im
`AddToPools`-Präfix gesetzt, im Postfix gelöscht), Stufe 1, Stufe 2. Damit sind die Teile eines
Pools alle im selben Entfernungsband sichtbar, ihre Ranges verschmelzen wieder, und ein Pool
ohne etwas im Band kostet keinen Draw-Call.

**Kein zusätzlicher Videospeicher.** Was einen Pool füllt, ist das Vertex-Budget (500.000), nicht
die Teilegrenze: der Feldreport zeigt 516 von 3000 möglichen Teilen je Pool. Die Bilder sind
kleiner, passen also mehr Teile in dasselbe Budget — die Pool-Zahl bleibt proportional zu den
Vertices. Genau das war der Grund, warum das Regions-Routing seinerzeit scheiterte (1.917 Pools
à 56 Teile, vierfacher Videospeicher); hier tritt er nicht ein, und die Lane kostet höchstens
einen halbvollen Pool je Lane und Manager.

### 2. Jedes Ausgabe-Mesh allozierte ein frisches int[]

`MeshData.Dispose` setzt `CustomInts` und `CustomShorts` auf null, **bevor** das Mesh in den
Recycler geht. Ein recyceltes Mesh kommt also grundsätzlich ohne sie an, und jedes der zwei
Bilder je Teil brauchte ein neues int[] (bei TopSoil dazu ein short[]). Die Alloc-Stichprobe:
**31 MB/s `Int32[]` auf dem Tesselations-Thread**. Sie kommen jetzt aus einer eigenen
Größenklassen-Ablage (`FarLod.Ints`/`.Shorts`, dieselbe `ArrayPoolByClass` wie die Extras des
Klon-Kompakts, eigene Budgets), und `FarLod.Release` gibt sie zurück, sobald `AddModel`
hochgeladen hat — der Upload ist synchron, die Arrays sind danach frei.

Gemessen im Bench gegen einen echten Recycler (`far lod build`): **45,6 KB je Chunk-Build ohne
Ablage, 0,8 KB mit**. Der Bench legt sich dafür einen `MeshDataRecycler` über einen
`DispatchProxy` an — ohne ihn allozierte jedes Ausgabe-Mesh auch seine Basis-Arrays, und die
Zahl hätte den Effekt verdeckt, um den es geht.

Der Doppelrückgabe-Fall ist die eine Falle: zwei Rückgaben desselben Arrays hieße, zwei Mieter
teilen sich eines. `Release` nullt die Custom-Parts vor `Dispose`, also gibt der zweite Aufruf
nichts zurück — und `Dispose` selbst setzt `Recyclable` zurück, recycelt das Mesh also auch
nicht zweimal. Der Test fährt genau das.

### 3. Teile, für die sich ein Bild nicht lohnt

Ein Dutzend Blumen in `BlendNoCull` kostete zwei Pool-Teile, zwei Einträge in jedem Sweep und
zwei Draw-Ranges, um eine Handvoll Dreiecke zu sparen. Unter **96 Flächen** bleibt ein Teil
jetzt ganz aus dem Build heraus und zeichnet sein eigenes Mesh in jeder Entfernung weiter. Seine
Blöcke sind dann nicht im Zellbild — was den Nachbarzellen nur Flächen **hinzufügen** kann
(ihre Nachbarzelle gilt als Luft), nie eine wegnehmen. Kein Loch, keine Delle.

### Was danach noch offen ist

Der Kamera-Pass ist weiter 60 % der GPU-Zeit: **30 Mio. Fragmente gegen 15 Mio. vorher** bei
3,7 Mio. Pixeln. Die Dreiecke sind gefallen, die Fragmente gestiegen — Zellen sind bis zu einen
Block dicker als die Blöcke, die sie ersetzen, also deckt derselbe Hügel mehr Pixel. Der Gewinn
ist vom Front-End in die Füllrate gewandert. Der nächste Hebel dort ist die Ferndistanz selbst
(`.komet farmesh <Blöcke>`): sie steht auf `max(400, 0,35 × Sichtweite)` = 538, und diesseits
davon zeichnet die Engine noch 2,7 Mio. Dreiecke. 350 statt 538 verschiebt davon gut die Hälfte
ins Zellbild — bezahlt mit einer sichtbaren Ein-Block-Stufe in 350 Blöcken Entfernung. Das ist
eine Entscheidung für Augen, nicht für einen Default.

