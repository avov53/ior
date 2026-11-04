# Components used by this prototype

- Discovery catalog: https://www.shadcn.io/icons and https://www.shadcn.io/blocks
- Free component references: https://www.shadcn.io/ui/button , https://www.shadcn.io/ui/card , https://www.shadcn.io/ui/item
- Upstream source: https://github.com/shadcn-ui/ui/tree/main/apps/v4/registry/new-york-v4/ui
- Button, Card, Item and Badge are HTML adaptations for this standalone prototype. React renders reusable markup templates at build time. Tailwind utility rules are translated into local component CSS; existing delegated event handlers perform actions. This is not an installation of the paid shadcn.io Pro block catalog.
- Lucide icons are rendered from the already installed lucide-react package. Only selected SVG icons are embedded. The browser requires no CDN, React runtime or extra icon library.
- Old handmade SVG definitions and server-list renderer were replaced. Previous layouts remain in checkpoints for rollback; soft-surfaces.css is no longer loaded by the build.
- Production React frontend and WebView2 commands are not connected to this prototype.

## shadcn/ui license

MIT License

Copyright (c) 2023 shadcn

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated documentation files (the "Software"), to deal in the Software without restriction, including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.

Lucide license: LICENSE-lucide.txt. Included in generated HTML too.

Official component references: https://ui.shadcn.com/docs/components/radix/switch and https://ui.shadcn.com/docs/components/radix/input . Switch and Input are standalone HTML adaptations; browser behavior is handled locally, not by Radix React runtime.

QR generation uses the already installed qrcode package, bundled locally with rolldown. See LICENSE-qrcode.txt. Native mobile-share envelopes are not reproduced.
