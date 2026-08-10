/**
 * Read an attachment for the inline model payload without placing the browser
 * file-reader implementation in the initial application bundle.
 */
export function readFileAsBase64(file: File): Promise<{ base64: string; mimeType: string } | null> {
  return new Promise((resolve) => {
    if (file.size > 10 * 1024 * 1024) {
      resolve(null);
      return;
    }

    const reader = new FileReader();
    reader.onload = () => {
      const result = typeof reader.result === 'string' ? reader.result : '';
      const comma = result.indexOf(',');
      const base64 = comma >= 0 ? result.substring(comma + 1) : result;
      const mimeMatch = /^data:([^;]+);base64,/.exec(result);
      const mimeType = mimeMatch?.[1] ?? file.type ?? 'image/png';
      resolve({ base64, mimeType });
    };
    reader.onerror = () => resolve(null);
    reader.readAsDataURL(file);
  });
}
