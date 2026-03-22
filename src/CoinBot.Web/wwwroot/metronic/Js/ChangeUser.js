

var _IsRegistry = "/Main/IsRegistryControl";


document.getElementById("IsRegistry").addEventListener("blur", function () {
    var asd = document.getElementById('IsRegistry').value;
    if (asd!="") {
        $.post(_IsRegistry + "?" + Date, { "Registry": asd }, function (a, b, c) {

            if (a.Success === true) {

                swal.fire({
                    title: '"' + asd + '" Sicil numarası farklı bir kullanıcıda kullanılmaktadır',
                    type: 'warning',
                    confirmButtonText: 'Tamam',
                    reverseButtons: true
                })
                document.getElementById("peer").value = "";
                document.getElementById("recbutton").disabled = true;
            } else {
                swal.fire({
                    title: '"' + asd + '" Sicil numarası kullanılabilir durumdadır.',
                    type: 'warning',
                    confirmButtonText: 'Tamam',
                    reverseButtons: true
                })
                //document.getElementById("recbutton").disabled = false;

                document.getElementById("peer").value = "oke";
            }
        });
    }
    removeSpaces();
});