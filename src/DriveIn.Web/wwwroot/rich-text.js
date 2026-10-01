// The rich text editor for theaters' pages and posts (Components/Shared/RichTextEditor.razor): Quill 2, loaded on first
// use from wwwroot/lib/quill. What it produces is only a draft: the server sanitizes it (HtmlContent) before storing
// or showing it. Images never go in as data: URLs; the toolbar button, paste and drop all hand files to .NET, which
// adds them to the theater's image library and inserts them by address.

const editors = new Map();
let nextId = 1;
let loading;

function loadQuill() {
    if (window.Quill) return Promise.resolve();
    loading ??= new Promise((resolve, reject) => {
        const css = document.createElement('link');
        css.rel = 'stylesheet';
        css.href = 'lib/quill/quill.snow.css';
        document.head.appendChild(css);
        const script = document.createElement('script');
        script.src = 'lib/quill/quill.js';
        script.onload = () => { registerImage(); resolve(); };
        script.onerror = () => reject(new Error('Could not load the editor.'));
        document.head.appendChild(script);
    });
    return loading;
}

// Quill's image keeps alt, width and height; ours also keeps its layout classes (img-small, img-left...).
const imageAttributes = ['alt', 'width', 'height', 'class'];
function registerImage() {
    const Image = window.Quill.import('formats/image');
    class LibraryImage extends Image {
        static formats(node) {
            const formats = {};
            for (const name of imageAttributes)
                if (node.hasAttribute(name)) formats[name] = node.getAttribute(name);
            return formats;
        }
        format(name, value) {
            if (imageAttributes.includes(name)) {
                if (value) this.domNode.setAttribute(name, value);
                else this.domNode.removeAttribute(name);
            } else {
                super.format(name, value);
            }
        }
    }
    window.Quill.register(LibraryImage, true);
}

const toolbar = [
    [{ header: [2, 3, 4, false] }],
    ['bold', 'italic', 'underline', 'strike'],
    [{ list: 'ordered' }, { list: 'bullet' }, { indent: '-1' }, { indent: '+1' }],
    ['blockquote', 'link'],
    [{ align: [] }],
    ['image'],
    ['clean'],
];

export async function create(host, html, placeholder, dotnet) {
    await loadQuill();
    const id = nextId++;
    const quill = new window.Quill(host, {
        theme: 'snow',
        placeholder: placeholder || '',
        modules: {
            toolbar: { container: toolbar, handlers: { image: () => openPicker(id) } },
        },
    });
    // Pasted HTML may carry data: images (or images from other sites); those are dropped here, and the server would
    // drop them anyway. Pasted image files are uploaded below instead.
    quill.clipboard.addMatcher('IMG', (node, delta) => {
        const src = node.getAttribute('src') || '';
        return /^(https?:\/\/[^/]+)?\/?theaters\/[a-z0-9-]+\/images\/\d+/.test(src) ? delta : new (window.Quill.import('delta'))();
    });
    // Quill would turn a pasted script's (or style's...) code into text; drop it instead.
    for (const tag of ['SCRIPT', 'STYLE', 'NOSCRIPT', 'TEMPLATE', 'IFRAME', 'OBJECT', 'EMBED', 'SVG', 'TITLE'])
        quill.clipboard.addMatcher(tag, () => new (window.Quill.import('delta'))());
    if (html) quill.setContents(quill.clipboard.convert({ html }), 'silent');

    const editor = { quill, dotnet, editing: null };
    editors.set(id, editor);

    // Paste and drop of image files: upload them (one at a time, in order) and insert each where the cursor is.
    const takeFiles = (e, files) => {
        const images = [...(files || [])].filter(f => f.type.startsWith('image/'));
        if (images.length === 0) return;
        e.preventDefault();
        e.stopPropagation();
        uploadFiles(id, images);
    };
    quill.root.addEventListener('paste', e => takeFiles(e, e.clipboardData?.files), true);
    quill.root.addEventListener('drop', e => takeFiles(e, e.dataTransfer?.files), true);

    // Clicking an image opens its settings (description, size, position) in .NET's dialog.
    quill.root.addEventListener('click', e => {
        if (e.target?.tagName !== 'IMG') return;
        const blot = window.Quill.find(e.target);
        if (!blot) return;
        editor.editing = blot;
        dotnet.invokeMethodAsync('EditImage', {
            src: e.target.getAttribute('src') || '',
            alt: e.target.getAttribute('alt') || '',
            className: e.target.getAttribute('class') || '',
        });
    });
    return id;
}

function openPicker(id) {
    const editor = editors.get(id);
    if (!editor) return;
    editor.range = editor.quill.getSelection(true);
    editor.dotnet.invokeMethodAsync('OpenImagePicker');
}

async function uploadFiles(id, files) {
    const editor = editors.get(id);
    if (!editor) return;
    for (const file of files) {
        editor.range = editor.quill.getSelection(true);
        const stream = DotNet.createJSStreamReference(file);
        try {
            await editor.dotnet.invokeMethodAsync('UploadDropped', stream, file.name, file.size);
        } catch {
            // .NET shows the reason (too large, wrong type...); stop at the first failure.
            return;
        }
    }
}

// Inserts an image at the cursor (or where the picker was opened from).
export function insertImage(id, image) {
    const editor = editors.get(id);
    if (!editor) return;
    const quill = editor.quill;
    const range = editor.range || quill.getSelection(true) || { index: quill.getLength(), length: 0 };
    if (range.length) quill.deleteText(range.index, range.length, 'user');
    quill.insertEmbed(range.index, 'image', image.src, 'user');
    quill.formatText(range.index, 1, { alt: image.alt || '', width: String(image.width), height: String(image.height), class: image.className }, 'user');
    quill.setSelection(range.index + 1, 0, 'silent');
    editor.range = null;
}

// Updates (or, with null, removes) the image last clicked.
export function updateImage(id, image) {
    const editor = editors.get(id);
    const blot = editor?.editing;
    if (!blot) return;
    const index = editor.quill.getIndex(blot);
    if (image) editor.quill.formatText(index, 1, { alt: image.alt || '', class: image.className }, 'user');
    else editor.quill.deleteText(index, 1, 'user');
    editor.editing = null;
}

// The editor's content as plain HTML: Quill's list markup (every list is an <ol> with data-list on its items) turned
// back into <ul>/<ol>, and its own UI bits removed. Empty if nothing was written.
export function getHtml(id) {
    const editor = editors.get(id);
    if (!editor) return null;
    const quill = editor.quill;
    if (quill.getLength() <= 1 && !quill.root.querySelector('img')) return '';
    const root = quill.root.cloneNode(true);
    root.querySelectorAll('.ql-ui').forEach(n => n.remove());
    root.querySelectorAll('[contenteditable]').forEach(n => n.removeAttribute('contenteditable'));
    for (const list of [...root.querySelectorAll('ol')]) {
        let current = null;
        for (const li of [...list.children]) {
            const tag = li.getAttribute('data-list') === 'bullet' ? 'UL' : 'OL';
            if (!current || current.tagName !== tag) {
                current = document.createElement(tag);
                list.parentNode.insertBefore(current, list);
            }
            li.removeAttribute('data-list');
            current.appendChild(li);
        }
        list.remove();
    }
    return root.innerHTML;
}

export function dispose(id) {
    editors.delete(id);
}
