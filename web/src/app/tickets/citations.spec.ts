import { Citation } from '../api/replies.models';
import { citedArticles, segmentReply } from './citations';

const shipping: Citation = { marker: 1, articleSlug: 'shipping-times', articleTitle: 'Shipping times' };
const returns: Citation = { marker: 2, articleSlug: 'returns-policy', articleTitle: 'Returns policy' };

describe('segmentReply', () => {
  it('turns valid markers into citations and keeps the text around them', () => {
    expect(segmentReply('Ships soon [1]. Returns [ 2 ]', [shipping, returns])).toEqual([
      { kind: 'text', text: 'Ships soon ' },
      { kind: 'cite', numbers: [{ marker: 1, citation: shipping }] },
      { kind: 'text', text: '. Returns ' },
      { kind: 'cite', numbers: [{ marker: 2, citation: returns }] },
    ]);
  });

  it('reads adjacent markers and lists', () => {
    const segments = segmentReply('[1][2] and [1, 2]', [shipping, returns]);

    expect(segments.map((s) => s.kind)).toEqual(['cite', 'cite', 'text', 'cite']);
    expect(segments[3]).toEqual({ kind: 'cite', numbers: [{ marker: 1, citation: shipping }, { marker: 2, citation: returns }] });
  });

  it('leaves removed markers as plain text', () => {
    expect(segmentReply('Made up [7], real [1].', [shipping])).toEqual([
      { kind: 'text', text: 'Made up [7], real ' },
      { kind: 'cite', numbers: [{ marker: 1, citation: shipping }] },
      { kind: 'text', text: '.' },
    ]);
  });

  it('keeps a removed number inside a list unlinked', () => {
    expect(segmentReply('[1, 9]', [shipping])).toEqual([
      { kind: 'cite', numbers: [{ marker: 1, citation: shipping }, { marker: 9, citation: undefined }] },
    ]);
  });

  it('is all text before the citations arrive', () => {
    expect(segmentReply('Ships soon [1].', [])).toEqual([{ kind: 'text', text: 'Ships soon [1].' }]);
  });
});

describe('citedArticles', () => {
  it('lists each article once with all its markers', () => {
    const again: Citation = { marker: 3, articleSlug: 'shipping-times', articleTitle: 'Shipping times' };

    expect(citedArticles([again, returns, shipping])).toEqual([
      { slug: 'shipping-times', title: 'Shipping times', markers: [1, 3] },
      { slug: 'returns-policy', title: 'Returns policy', markers: [2] },
    ]);
  });
});
