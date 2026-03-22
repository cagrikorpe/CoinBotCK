var _IsActive = "/Main/TempalateDell";
var _AddTemplate = "/Main/TemlateAdd";
var _TemlateAddValue = "/Main/TemlateAddValue";
var _OUValue = "/Main/OUValue";
var _AddGroup = "/Main/AddGroup";

function DeleteItem(itm) {
  var asd = $(itm).attr("data-id");
  swal.fire({
    title: 'Temlate silme işlemi yapmak istediğinizde eminmisiniz?',
    text: "Bu işlemi geri alamayacaksın!",
    type: 'warning',
    showCancelButton: true,
    confirmButtonText: 'Evet!',
    cancelButtonText: 'Hayır!',
    reverseButtons: true
  }).then(function (result) {
    if (result.value) {
      $.post(_IsActive + "?" + Date, { "Id": asd }, function (a, b, c) {
        toastr.options = {
          "closeButton": false,
          "debug": false,
          "newestOnTop": false,
          "progressBar": false,
          "positionClass": "toast-top-right",
          "preventDuplicates": false,
          "onclick": null,
          "showDuration": "300",
          "hideDuration": "1000",
          "timeOut": "5000",
          "extendedTimeOut": "1000",
          "showEasing": "swing",
          "hideEasing": "linear",
          "showMethod": "fadeIn",
          "hideMethod": "fadeOut"
        };
        toastr.success("Template Silinmiştir.");
        setTimeout(function () {
          location.reload();
        }, 500);
        //location.reload();
      });

    }
  });
}


function EditItem(itm) {
  var asd = $(itm).attr("data-id");
  document.getElementById("Attid").value = asd;
}

function OuItem(itm) {
  var asd = $(itm).attr("data-id");
    document.getElementById("OuAttid").value = asd;
}

function GrpItem(itm) {
  var asd = $(itm).attr("data-id");
    document.getElementById("GrpAttid").value = asd;
}


function AddTemplate(item) {

  
  var nnn = document.getElementById("templateName").value;


  $.post(_AddTemplate + "?" + Date, { "Name": nnn }, function (data, status, xhr) {
    toastr.success("Kayıt İşlemi Başarılı", "İşlem");
    setTimeout(
      function () {
        location.reload(true);
        }, 550);


  }).fail(function () {
    toastr.error("Kayıt İşlemi Başarısız", "İşlem");
  });

}



function AddTemplateValue(item) {


    var Idd = document.getElementById("Attid").value;
    var Namee = document.getElementById("Attname").value;
    var AttValuee = document.getElementById("AttValue").value;


    $.post(_TemlateAddValue + "?" + Date, { "Id": Idd, "Name": Namee, "AttValue": AttValuee }, function (data, status, xhr) {
        toastr.success("Kayıt İşlemi Başarılı", "İşlem");
        document.getElementById("Attid").value = "";
        document.getElementById("Attname").value = "";
        document.getElementById("AttValue").value = "";
        setTimeout(
            function () {
                location.reload(true);
            }, 550);


    }).fail(function () {
        toastr.error("Kayıt İşlemi Başarısız", "İşlem");
    });

}




function OUValue(item) {


    var Idd = document.getElementById("OuAttid").value;
    var Ou = document.getElementById("moveOu").value;


    $.post(_OUValue + "?" + Date, { "Id": Idd, "Ou": Ou}, function (data, status, xhr) {
        toastr.success("Kayıt İşlemi Başarılı", "İşlem");
        document.getElementById("Attid").value = "";
        document.getElementById("Attname").value = "";
        document.getElementById("AttValue").value = "";
        setTimeout(
            function () {
                location.reload(true);
            }, 550);


    }).fail(function () {
        toastr.error("Kayıt İşlemi Başarısız", "İşlem");
    });

}




function AddGroup(item) {
    alert();

    var Idd = document.getElementById("GrpAttid").value;
    var Namee = document.getElementById("GroupName").value;


    $.post(_AddGroup + "?" + Date, { "Id": Idd, "Name": Namee}, function (data, status, xhr) {
        toastr.success("Kayıt İşlemi Başarılı", "İşlem");
        document.getElementById("GrpAttid").value = "";
        document.getElementById("GroupName").value = "";
        setTimeout(
            function () {
                location.reload(true);
            }, 550);


    }).fail(function () {
        toastr.error("Kayıt İşlemi Başarısız", "İşlem");
    });

}


