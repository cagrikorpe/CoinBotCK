(function () {
    const container = document.getElementById('cb-authenticator-qr');

    if (!container || !container.dataset.cbQrMatrix) {
        return;
    }

    let matrix;

    try {
        matrix = JSON.parse(container.dataset.cbQrMatrix);
    } catch {
        return;
    }

    if (!Array.isArray(matrix) || matrix.length === 0) {
        return;
    }

    const quietZone = 4;
    const moduleSize = 4;
    const moduleCount = matrix.length + (quietZone * 2);
    const size = moduleCount * moduleSize;
    const svgNamespace = 'http://www.w3.org/2000/svg';
    const svg = document.createElementNS(svgNamespace, 'svg');

    svg.setAttribute('xmlns', svgNamespace);
    svg.setAttribute('viewBox', `0 0 ${moduleCount} ${moduleCount}`);
    svg.setAttribute('width', String(size));
    svg.setAttribute('height', String(size));
    svg.setAttribute('role', 'img');
    svg.setAttribute('aria-label', 'Authenticator QR kodu');
    svg.setAttribute('shape-rendering', 'crispEdges');
    svg.classList.add('d-block');

    const background = document.createElementNS(svgNamespace, 'rect');
    background.setAttribute('x', '0');
    background.setAttribute('y', '0');
    background.setAttribute('width', String(moduleCount));
    background.setAttribute('height', String(moduleCount));
    background.setAttribute('fill', '#ffffff');
    svg.appendChild(background);

    for (let rowIndex = 0; rowIndex < matrix.length; rowIndex++) {
        const row = matrix[rowIndex];

        if (typeof row !== 'string') {
            continue;
        }

        for (let columnIndex = 0; columnIndex < row.length; columnIndex++) {
            if (row[columnIndex] !== '1') {
                continue;
            }

            const module = document.createElementNS(svgNamespace, 'rect');
            module.setAttribute('x', String(columnIndex + quietZone));
            module.setAttribute('y', String(rowIndex + quietZone));
            module.setAttribute('width', '1');
            module.setAttribute('height', '1');
            module.setAttribute('fill', '#111827');
            svg.appendChild(module);
        }
    }

    container.innerHTML = '';
    container.appendChild(svg);
})();
