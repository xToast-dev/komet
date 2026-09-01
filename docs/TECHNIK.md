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

Die Zeile `= ausserhalb ... davon swap` im HUD und die `[swap …]`-Spalte im Stresstest sind
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
Einschalten des Prefetch, wirkt er. Die Zeile kommt aus `shared/` und erscheint auch in der
Baseline — Vorher/Nachher ist damit ein Mod-Manager-Toggle.

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
Grading. Sie stehen jetzt als `post/compose` in einer Zeile. Dazu `= ausserhalb`: die Zeit, die
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
entscheidbar: `= ausserhalb` fängt nur Wartezeit am Buffer-Swap, Rückstau *innerhalb* der
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
Das Hitch-Log (`shared/HitchLog.cs`) bucht deshalb **jeden** Frame, der die Schwelle reißt
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

Das Log liegt in `shared/` und läuft in der Baseline identisch mit — „ruckler/min vanilla
gegen komet" ist damit per Konstruktion vergleichbar. Außerdem seit 1.31.0: HUD und
`.komet` nennen den **GC-Modus**.

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

**Beifang desselben Logs, ein Messfehler seit 1.0:** `tick 26,6 ms` in einem 26,2-ms-Frame.
`CoreServerEventManager.TriggerGameTick` ruft `base.TriggerGameTick` — die gepatchte
Methode — auf dem **Server-Thread** (Singleplayer), und dessen Ticks landeten mit in der
Client-Frame-Buchhaltung. Seit 1.32.0 bucht der Postfix nur noch, wenn die übergebene Welt
`ClientMain` ist; die `game tick`-Zeile ist im Singleplayer jetzt ehrlicher (tendenziell
kleiner), besonders während Chunk-Fluten.

## „ausserhalb … davon swap": wo die Treiberzeit wirklich sitzt

Mit mesa_glthread (radeonsi-Default) messen alle Stage-Zeiten nur das **Einreihen** der
GL-Befehle — der Treiber-Thread führt asynchron aus. Die echte Treiberarbeit wird dort
bezahlt, wo die Queue leerlaufen muss, und der eine garantierte Drain-Punkt jedes Frames ist
`SwapBuffers`. Ein `ausserhalb` von 2,5 ms bei 32 % GPU-Auslastung ist also entweder
Treiber-Thread-CPU (Befehle ausführen), Compositor-Gegendruck — oder schlicht die
Event-Schleife. Seit 1.22.0 wird der Swap selbst gemessen: die HUD-Zeile zeigt
`= ausserhalb … davon swap X`, und im Worst-Frame-Tail trennen sich `swap` und `draussen`
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

Vier Blöcke statt einer flachen Liste: **Kopf** (fps, gpu-frame, schlechtester Frame mit
Aufteilung, Ruckler), **frame-aufteilung** (jeder Bucket des Frames in der Reihenfolge und
mit dem Vokabular des Hitch-Logs — inklusive game tick und `ausserhalb` summiert der Block
auf 100 %), **gc** (Pausen, Alloc-Quellen, Modus) und **welt & laden** (Draw Calls,
Chunks, Tesselation, VRAM, Upload). Neben jedem Frame-Bucket steht ein Balken: zehn Zellen
sind der ganze Frame, Achtel-Zellen über die Unicode-Blockelemente. Ob die Blockglyphen in
der Monospace-Zelle des Systems wirklich eine Zelle breit sind, wird einmal beim
Metrik-Probing gemessen; weicht der Font ab, degradieren die Balken zu `#`, statt aus der
Box zu laufen (die Rasterbreite rechnet in Zeichenzellen). Die Schatten-Zeilen der
Aufteilung enthalten jetzt auch die Done-Hälften der Kaskaden, damit zwischen den Zeilen
nichts mehr versteckt ist; die Drossel-Zeile der Komet-Sektion heißt `schatten-takt`,
damit kein Name zwei Bedeutungen hat.

Neben `Komet.dll` wird `KometBaseline.dll` mit installiert. Die enthält **nur die Messung**
und **keine einzige Optimierung** — dasselbe HUD, aus buchstäblich denselben Quelldateien
(`shared/`), damit eine Zahl hier und eine Zahl dort dasselbe bedeuten und sich subtrahieren
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
| `MeasureRenderStages` | `true` | Zeit pro Render-Stage und Game-Tick (2 Stopwatch-Lesungen × ~13 Stages) |
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
| `AnimationLookupWithoutAlloc` | `true` | |
| `AnimationCollisionBoxWithoutAlloc` | `true` | |
| `SkipPerFrameGlErrorCheck` | `false` | siehe oben |
| `StatsLogIntervalSeconds` | `0` | Zähler periodisch ins Log |

Schlägt ein Patch fehl (z. B. nach einem VS-Update), wird das geloggt, die betroffene
Optimierung übersprungen und das Spiel läuft normal weiter.

---

## Projektstruktur

```
src/FastCuller.cs              Sichtbarkeits-Sweep: SoA-Cache, Cull-Loops, Range-Merging
src/RayTraversal.cs            Chunk-Raywalk mit aus der Schleife gehobenen Konstanten
src/FastChunkCuller.cs         Occlusion-Pass: Snapshot, flaches Gitter, parallel
src/UploadBudget.cs            Regler für die Upload-Zeit pro Frame
shared/FrameStats.cs           Pro-Frame-Buchführung, exponentiell geglättet
shared/DebugHud.cs             das Overlay (Ortho-Stage, Text-Textur alle 250 ms erneuert)
shared/MeasurementPatches.cs   reine Zeitmessung — auch von der Baseline-Mod benutzt
baseline/                      die Vanilla-Messlatte: shared/ + ModSystem, sonst nichts
src/Patches/                   Harmony-Einsprungpunkte
src/KometModSystem.cs          Laden, Config, .komet-Command
verify/                        wendet die echten Patches auf die echten Assemblies an,
                               erzwingt JIT und prüft Verhalten — ohne das Spiel zu starten
bench/                         Äquivalenz- und Durchsatzmessung gegen Vanilla
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
* ~~**Lücken-Merging bei Draw-Ranges.**~~ Bis 1.49 stand hier „den Aufwand nicht wert" —
  begründet mit einer Szene, die damals GPU-gebunden war. Die 1.47/1.48-Reports zeigten das
  Gegenteil (gpu ~2,5 ms von ~13 ms Frame, 15.749 Ranges über 929 Draw-Calls), also ist es
  seit 1.50.0 gebaut: siehe „Lücken-Merging" unten. Die Unterscheidung aus der alten
  Begründung gilt darin unverändert — über *frustum*-verworfene Teile wird verschmolzen (die
  GPU clippt sie, null Fragmente), über *distanz*-verworfene, versteckte, occlusion-verdeckte
  oder freie Bytes nie.

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
