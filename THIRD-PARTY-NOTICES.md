# Software von Dritten

Zwijg selbst steht unter der GNU AGPL 3.0 mit Zusatzbedingungen (siehe `LICENSE` und `NOTICE`). Es nutzt die folgenden Bibliotheken. Alle stehen unter freien Lizenzen, die Nutzung, Veränderung und Weitergabe erlauben, auch kommerziell, und sind mit der AGPL vereinbar. Bedingung ist, dass die Hinweise und Lizenztexte mitgeliefert werden. Dafür ist diese Datei da.

## Im Gateway enthalten

| Paket | Version | Lizenz | Rechteinhaber | Projekt |
|---|---|---|---|---|
| .NET und ASP.NET Core | 9 | MIT | .NET Foundation und Mitwirkende | https://github.com/dotnet |
| Anthropic C# SDK | 12.51.0 | MIT | Anthropic | https://github.com/anthropics/anthropic-sdk-csharp |
| Microsoft.Extensions.AI.Abstractions | 10.5.1 | MIT | Microsoft Corporation | https://github.com/dotnet/extensions |
| Microsoft.Data.Sqlite | 10.0.12 | MIT | Microsoft Corporation | https://github.com/dotnet/efcore |
| System.Text.Json, System.Text.Encodings.Web, System.IO.Pipelines, System.Net.ServerSentEvents, System.Memory | 10.0.x, 4.5.3 | MIT | Microsoft Corporation, .NET Foundation | https://github.com/dotnet/runtime |
| PdfPig | 0.1.16 | Apache 2.0, enthält Teile unter BSD und der Adobe AFM Lizenz (siehe unten) | UglyToad und Mitwirkende | https://github.com/UglyToad/PdfPig |
| SQLitePCLRaw | 2.1.12 | Apache 2.0 | SourceGear, LLC | https://github.com/ericsink/SQLitePCL.raw |
| SQLite (in SQLitePCLRaw enthalten) | 3.x | Public Domain | D. Richard Hipp und andere | https://sqlite.org |
| Docnet.Core | 2.6.0 | MIT | Modestas Petravicius | https://github.com/GowenGit/docnet |
| SharpZipLib | 1.4.2 | MIT | ICSharpCode und Mitwirkende | https://github.com/icsharpcode/SharpZipLib |
| Whisper.net, Whisper.net.Runtime | 1.9.1 | MIT | Sandro Hanea | https://github.com/sandrohanea/whisper.net |
| whisper.cpp und ggml (in Whisper.net.Runtime enthalten) | | MIT | Georgi Gerganov und Mitwirkende | https://github.com/ggerganov/whisper.cpp |
| PDFium (in Docnet.Core enthalten) | | BSD 3-Clause, enthält Teile unter weiteren freien Lizenzen (siehe `licenses/PDFium.txt`) | The PDFium Authors | https://pdfium.googlesource.com/pdfium |

## Daten

| Daten | Lizenz | Quelle |
|---|---|---|
| Ortsliste `orte-alle.txt` (Gemeinden, Ortsteile, Landkreise in Deutschland) | CC BY 4.0 | GeoNames, https://www.geonames.org |

Die Ortsliste wurde aus den Daten von GeoNames erzeugt (https://download.geonames.org/export/dump/DE.zip) und dabei verändert: nur bewohnte Orte, Gemeinden und Kreise, Zusätze wie "Landkreis" zusätzlich entfernt, Orte, die auch häufige Wörter oder Namen sind, markiert oder weggelassen. Das Skript dazu liegt unter `scripts/daten/build-orte.js`. Lizenz: Creative Commons Namensnennung 4.0 International, https://creativecommons.org/licenses/by/4.0/deed.de

## Nur für die Tests

Diese Pakete werden zum Testen gebraucht und sind im fertigen Gateway nicht enthalten.

| Paket | Version | Lizenz |
|---|---|---|
| xunit | 2.9.2 | Apache 2.0 |
| xunit.runner.visualstudio | 2.8.2 | Apache 2.0 |
| Microsoft.NET.Test.Sdk | 17.12.0 | MIT |
| Microsoft.AspNetCore.Mvc.Testing | 9.0.x | MIT |
| coverlet.collector | 6.0.2 | MIT |
| Newtonsoft.Json (über Test SDK) | 13.0.1 | MIT |

## Genutzt, aber nicht enthalten

Diese Programme und Dienste liefert Zwijg nicht mit. Wer sie einsetzt, lädt sie selbst herunter und muss deren Bedingungen beachten.

- **Ollama** (MIT), https://github.com/ollama/ollama
- **Tesseract** für die Texterkennung (Apache 2.0), https://github.com/tesseract-ocr/tesseract. Im Docker Image ist es aus den Debian Paketen mit installiert, die Lizenzen liegen dort unter `/usr/share/doc`.
- **Whisper Modelle** für das Diktieren (MIT, OpenAI). Zwijg lädt sie erst auf Wunsch des Admins im ggml Format von https://huggingface.co/ggerganov/whisper.cpp herunter.
- **Sprachmodelle** wie qwen2.5:7b, llama3.1 oder gemma2 haben jeweils eigene Lizenzen. Qwen2.5 7B steht unter Apache 2.0, Llama und Gemma unter eigenen Lizenzen von Meta und Google. Bitte vor dem Einsatz beim jeweiligen Modell nachlesen.
- Für **Cloud Anbieter** wie Anthropic, OpenAI, Mistral oder Google gelten deren Nutzungsbedingungen. Für Patientendaten braucht es außerdem einen Vertrag zur Auftragsverarbeitung.
- **Docker Images** (`mcr.microsoft.com/dotnet/aspnet`, `ollama/ollama`) enthalten weitere Software mit eigenen Lizenzen.

Die Symbole in der Oberfläche sind selbst gezeichnet. Die Listen mit Vornamen, Nachnamen, medizinischen Begriffen und die kleine Ortsliste `orte.txt` sind selbst zusammengestellt.

## Hinweise der Pakete

Einige Pakete liefern eigene Hinweise mit, die bei jeder Weitergabe dabei sein müssen. Sie stehen hier unverändert.

### PdfPig: NOTICES.txt

```
This product is derived from software developed at
The Apache Software Foundation (http://www.apache.org/).

Based on source code originally developed in the PDFBox and
FontBox projects.

Copyright (c) 2002-2007, www.pdfbox.org

Includes the Adobe Glyph List
Copyright 1997, 1998, 2002, 2007, 2010 Adobe Systems Incorporated.

Includes the Zapf Dingbats Glyph List
Copyright 2002, 2010 Adobe Systems Incorporated.
```

### PdfPig: externe Bestandteile (aus der LICENSE Datei von PdfPig)

```
EXTERNAL COMPONENTS

PdfPig includes a number of components with separate copyright notices
and license terms. Your use of these components is subject to the terms and
conditions of the following licenses.

Contributions made to the original PDFBox and FontBox projects:

   Copyright (c) 2002-2007, www.pdfbox.org
   All rights reserved.

   Redistribution and use in source and binary forms, with or without
   modification, are permitted provided that the following conditions are met:

   1. Redistributions of source code must retain the above copyright notice,
      this list of conditions and the following disclaimer.

   2. Redistributions in binary form must reproduce the above copyright
      notice, this list of conditions and the following disclaimer in the
      documentation and/or other materials provided with the distribution.

   3. Neither the name of pdfbox; nor the names of its contributors may be
      used to endorse or promote products derived from this software without
      specific prior written permission.

   THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS"
   AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE
   IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE
   ARE DISCLAIMED.  IN NO EVENT SHALL THE REGENTS OR CONTRIBUTORS BE LIABLE
   FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL
   DAMAGES (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR
   SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER
   CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT
   LIABILITY, OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY
   OUT OF THE USE OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF
   SUCH DAMAGE.

Adobe Font Metrics (AFM) for PDF Core 14 Fonts

   This file and the 14 PostScript(R) AFM files it accompanies may be used,
   copied, and distributed for any purpose and without charge, with or without
   modification, provided that all copyright notices are retained; that the
   AFM files are not distributed without this file; that all modifications
   to this file or any of the AFM files are prominently noted in the modified
   file(s); and that this paragraph is not modified. Adobe Systems has no
   responsibility or obligation to support the use of the AFM files.

CMaps for PDF Fonts (http://opensource.adobe.com/wiki/display/cmap/Downloads)

   Copyright 1990-2009 Adobe Systems Incorporated.
   All rights reserved.

   Redistribution and use in source and binary forms, with or without
   modification, are permitted provided that the following conditions
   are met:

   Redistributions of source code must retain the above copyright notice,
   this list of conditions and the following disclaimer.

   Redistributions in binary form must reproduce the above copyright notice,
   this list of conditions and the following disclaimer in the documentation
   and/or other materials provided with the distribution.

   Neither the name of Adobe Systems Incorporated nor the names of its
   contributors may be used to endorse or promote products derived from this
   software without specific prior written permission.

   THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS"
   AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE
   IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE
   ARE DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT HOLDER OR CONTRIBUTORS BE
   LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR
   CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF
   SUBSTITUTE GOODS OR SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS
   INTERRUPTION) HOWEVER CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN
   CONTRACT, STRICT LIABILITY, OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE)
   ARISING IN ANY WAY OUT OF THE USE OF THIS SOFTWARE, EVEN IF ADVISED OF
   THE POSSIBILITY OF SUCH DAMAGE.
```

### SQLitePCLRaw: NOTICE.TXT

```
----------------------------------------------------------------
Copyright on SQLitePCLRaw
----------------------------------------------------------------

Version prior to 2.0 were labeled with the copyright owned by
Zumero.  In 2.0, this changed to SourceGear.  There is no legal
distinction, as Zumero is simply a dba name for SourceGear.

And in either case, the open source license remains the same,
Apache v2.

----------------------------------------------------------------
License for SQLite
----------------------------------------------------------------

** The author disclaims copyright to this source code.  In place of
** a legal notice, here is a blessing:
**
**    May you do good and not evil.
**    May you find forgiveness for yourself and forgive others.
**    May you share freely, never taking more than you give.
**


----------------------------------------------------------------
License for MS Open Tech
----------------------------------------------------------------

// Copyright © Microsoft Open Technologies, Inc.
// All Rights Reserved
// Licensed under the Apache License, Version 2.0 (the "License"); you may not
// use this file except in compliance with the License. You may obtain a copy
// of the License at
// http://www.apache.org/licenses/LICENSE-2.0
//
// THIS CODE IS PROVIDED ON AN *AS IS* BASIS, WITHOUT WARRANTIES OR CONDITIONS
// OF ANY KIND, EITHER EXPRESS OR IMPLIED, INCLUDING WITHOUT LIMITATION ANY
// IMPLIED WARRANTIES OR CONDITIONS OF TITLE, FITNESS FOR A PARTICULAR PURPOSE,
// MERCHANTABLITY OR NON-INFRINGEMENT.
//
// See the Apache 2 License for the specific language governing permissions and
// limitations under the License.
```

## Lizenztexte

### MIT

Gilt für alle Pakete oben mit MIT Lizenz. Der Rechteinhaber steht jeweils in der Tabelle.

```
Copyright (c) <Rechteinhaber>

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

### SQLite

```
The author disclaims copyright to this source code. In place of
a legal notice, here is a blessing:

   May you do good and not evil.
   May you find forgiveness for yourself and forgive others.
   May you share freely, never taking more than you give.
```

### Apache 2.0

Gilt für PdfPig, SQLitePCLRaw und xunit.

```
                                 Apache License
                           Version 2.0, January 2004
                        http://www.apache.org/licenses/

   TERMS AND CONDITIONS FOR USE, REPRODUCTION, AND DISTRIBUTION

   1. Definitions.

      "License" shall mean the terms and conditions for use, reproduction,
      and distribution as defined by Sections 1 through 9 of this document.

      "Licensor" shall mean the copyright owner or entity authorized by
      the copyright owner that is granting the License.

      "Legal Entity" shall mean the union of the acting entity and all
      other entities that control, are controlled by, or are under common
      control with that entity. For the purposes of this definition,
      "control" means (i) the power, direct or indirect, to cause the
      direction or management of such entity, whether by contract or
      otherwise, or (ii) ownership of fifty percent (50%) or more of the
      outstanding shares, or (iii) beneficial ownership of such entity.

      "You" (or "Your") shall mean an individual or Legal Entity
      exercising permissions granted by this License.

      "Source" form shall mean the preferred form for making modifications,
      including but not limited to software source code, documentation
      source, and configuration files.

      "Object" form shall mean any form resulting from mechanical
      transformation or translation of a Source form, including but
      not limited to compiled object code, generated documentation,
      and conversions to other media types.

      "Work" shall mean the work of authorship, whether in Source or
      Object form, made available under the License, as indicated by a
      copyright notice that is included in or attached to the work
      (an example is provided in the Appendix below).

      "Derivative Works" shall mean any work, whether in Source or Object
      form, that is based on (or derived from) the Work and for which the
      editorial revisions, annotations, elaborations, or other modifications
      represent, as a whole, an original work of authorship. For the purposes
      of this License, Derivative Works shall not include works that remain
      separable from, or merely link (or bind by name) to the interfaces of,
      the Work and Derivative Works thereof.

      "Contribution" shall mean any work of authorship, including
      the original version of the Work and any modifications or additions
      to that Work or Derivative Works thereof, that is intentionally
      submitted to Licensor for inclusion in the Work by the copyright owner
      or by an individual or Legal Entity authorized to submit on behalf of
      the copyright owner. For the purposes of this definition, "submitted"
      means any form of electronic, verbal, or written communication sent
      to the Licensor or its representatives, including but not limited to
      communication on electronic mailing lists, source code control systems,
      and issue tracking systems that are managed by, or on behalf of, the
      Licensor for the purpose of discussing and improving the Work, but
      excluding communication that is conspicuously marked or otherwise
      designated in writing by the copyright owner as "Not a Contribution."

      "Contributor" shall mean Licensor and any individual or Legal Entity
      on behalf of whom a Contribution has been received by Licensor and
      subsequently incorporated within the Work.

   2. Grant of Copyright License. Subject to the terms and conditions of
      this License, each Contributor hereby grants to You a perpetual,
      worldwide, non-exclusive, no-charge, royalty-free, irrevocable
      copyright license to reproduce, prepare Derivative Works of,
      publicly display, publicly perform, sublicense, and distribute the
      Work and such Derivative Works in Source or Object form.

   3. Grant of Patent License. Subject to the terms and conditions of
      this License, each Contributor hereby grants to You a perpetual,
      worldwide, non-exclusive, no-charge, royalty-free, irrevocable
      (except as stated in this section) patent license to make, have made,
      use, offer to sell, sell, import, and otherwise transfer the Work,
      where such license applies only to those patent claims licensable
      by such Contributor that are necessarily infringed by their
      Contribution(s) alone or by combination of their Contribution(s)
      with the Work to which such Contribution(s) was submitted. If You
      institute patent litigation against any entity (including a
      cross-claim or counterclaim in a lawsuit) alleging that the Work
      or a Contribution incorporated within the Work constitutes direct
      or contributory patent infringement, then any patent licenses
      granted to You under this License for that Work shall terminate
      as of the date such litigation is filed.

   4. Redistribution. You may reproduce and distribute copies of the
      Work or Derivative Works thereof in any medium, with or without
      modifications, and in Source or Object form, provided that You
      meet the following conditions:

      (a) You must give any other recipients of the Work or
          Derivative Works a copy of this License; and

      (b) You must cause any modified files to carry prominent notices
          stating that You changed the files; and

      (c) You must retain, in the Source form of any Derivative Works
          that You distribute, all copyright, patent, trademark, and
          attribution notices from the Source form of the Work,
          excluding those notices that do not pertain to any part of
          the Derivative Works; and

      (d) If the Work includes a "NOTICE" text file as part of its
          distribution, then any Derivative Works that You distribute must
          include a readable copy of the attribution notices contained
          within such NOTICE file, excluding those notices that do not
          pertain to any part of the Derivative Works, in at least one
          of the following places: within a NOTICE text file distributed
          as part of the Derivative Works; within the Source form or
          documentation, if provided along with the Derivative Works; or,
          within a display generated by the Derivative Works, if and
          wherever such third-party notices normally appear. The contents
          of the NOTICE file are for informational purposes only and
          do not modify the License. You may add Your own attribution
          notices within Derivative Works that You distribute, alongside
          or as an addendum to the NOTICE text from the Work, provided
          that such additional attribution notices cannot be construed
          as modifying the License.

      You may add Your own copyright statement to Your modifications and
      may provide additional or different license terms and conditions
      for use, reproduction, or distribution of Your modifications, or
      for any such Derivative Works as a whole, provided Your use,
      reproduction, and distribution of the Work otherwise complies with
      the conditions stated in this License.

   5. Submission of Contributions. Unless You explicitly state otherwise,
      any Contribution intentionally submitted for inclusion in the Work
      by You to the Licensor shall be under the terms and conditions of
      this License, without any additional terms or conditions.
      Notwithstanding the above, nothing herein shall supersede or modify
      the terms of any separate license agreement you may have executed
      with Licensor regarding such Contributions.

   6. Trademarks. This License does not grant permission to use the trade
      names, trademarks, service marks, or product names of the Licensor,
      except as required for reasonable and customary use in describing the
      origin of the Work and reproducing the content of the NOTICE file.

   7. Disclaimer of Warranty. Unless required by applicable law or
      agreed to in writing, Licensor provides the Work (and each
      Contributor provides its Contributions) on an "AS IS" BASIS,
      WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or
      implied, including, without limitation, any warranties or conditions
      of TITLE, NON-INFRINGEMENT, MERCHANTABILITY, or FITNESS FOR A
      PARTICULAR PURPOSE. You are solely responsible for determining the
      appropriateness of using or redistributing the Work and assume any
      risks associated with Your exercise of permissions under this License.

   8. Limitation of Liability. In no event and under no legal theory,
      whether in tort (including negligence), contract, or otherwise,
      unless required by applicable law (such as deliberate and grossly
      negligent acts) or agreed to in writing, shall any Contributor be
      liable to You for damages, including any direct, indirect, special,
      incidental, or consequential damages of any character arising as a
      result of this License or out of the use or inability to use the
      Work (including but not limited to damages for loss of goodwill,
      work stoppage, computer failure or malfunction, or any and all
      other commercial damages or losses), even if such Contributor
      has been advised of the possibility of such damages.

   9. Accepting Warranty or Additional Liability. While redistributing
      the Work or Derivative Works thereof, You may choose to offer,
      and charge a fee for, acceptance of support, warranty, indemnity,
      or other liability obligations and/or rights consistent with this
      License. However, in accepting such obligations, You may act only
      on Your own behalf and on Your sole responsibility, not on behalf
      of any other Contributor, and only if You agree to indemnify,
      defend, and hold each Contributor harmless for any liability
      incurred by, or claims asserted against, such Contributor by reason
      of your accepting any such warranty or additional liability.

   END OF TERMS AND CONDITIONS
```
