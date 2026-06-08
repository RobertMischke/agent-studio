import { describe, expect, it } from 'vitest';
import { resolveProtocolImageSrc } from './protocol-image-resolver';

describe('resolveProtocolImageSrc', () => {
  it('maps attachments/<name> to the attachments endpoint', () => {
    expect(resolveProtocolImageSrc('attachments/abc.png', 'task-1', 'C:/work'))
      .toBe('/api/tasks/task-1/attachments/abc.png?watchPath=C%3A%2Fwork');
  });

  it('maps results/<name> to the results endpoint', () => {
    expect(resolveProtocolImageSrc('results/proof.png', 'task-1', 'C:/work'))
      .toBe('/api/tasks/task-1/results/proof.png?watchPath=C%3A%2Fwork');
  });

  it('treats bare filenames as results/<name> for legacy protocols', () => {
    expect(resolveProtocolImageSrc('proof.png', 'task-1', 'C:/work'))
      .toBe('/api/tasks/task-1/results/proof.png?watchPath=C%3A%2Fwork');
  });

  it('omits watchPath query when none is given', () => {
    expect(resolveProtocolImageSrc('results/proof.png', 'task-1', null))
      .toBe('/api/tasks/task-1/results/proof.png');
  });

  it('passes absolute URLs through unchanged', () => {
    expect(resolveProtocolImageSrc('https://cdn/x.png', 'task-1', 'C:/work'))
      .toBe('https://cdn/x.png');
  });

  it('passes data URIs through unchanged', () => {
    expect(resolveProtocolImageSrc('data:image/png;base64,AAA', 'task-1', null))
      .toBe('data:image/png;base64,AAA');
  });

  it('rejects path traversal in the prefix branches', () => {
    expect(resolveProtocolImageSrc('results/../etc/passwd', 'task-1', null))
      .toBe('results/../etc/passwd');
    expect(resolveProtocolImageSrc('attachments/sub/file.png', 'task-1', null))
      .toBe('attachments/sub/file.png');
  });

  it('falls back to the input when jobId is missing', () => {
    expect(resolveProtocolImageSrc('results/proof.png', '', 'C:/work'))
      .toBe('results/proof.png');
  });
});
